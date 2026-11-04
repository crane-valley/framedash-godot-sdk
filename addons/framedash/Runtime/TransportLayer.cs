#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using Godot;

namespace Framedash
{
	/// <summary>
	/// This is NOT a Node: it is constructed with an injected owner Node (the autoload) because
	/// Godot's HttpRequest must live in the scene tree and the retry-delay wait is driven by a
	/// SceneTreeTimer. The owner also provides the SceneTree.
	/// </summary>
	public sealed partial class TransportLayer : IDisposable
	{
		private readonly Node _owner;
		private readonly HttpRequest _http;
		private readonly string _endpointUrl;
		private readonly string _apiKey;
		private readonly string _sdkVersion;
		private readonly int _maxPayloadBytes;
		private readonly RetryPolicy _retryPolicy;
		private readonly bool _disabled;
		private sealed class ServerTimeObservation
		{
			internal readonly long ServerTimeUs;
			internal readonly long LocalTimeUs;
			internal readonly long StopwatchTicks;

			internal ServerTimeObservation(
				long serverTimeUs,
				long localTimeUs,
				long stopwatchTicks)
			{
				ServerTimeUs = serverTimeUs;
				LocalTimeUs = localTimeUs;
				StopwatchTicks = stopwatchTicks;
			}
		}
		private ServerTimeObservation? _serverTimeObservation;
		// A live blocking resend may share this mutable array with the parked async
		// request only after the root envelope's bytes are fixed; before then it must
		// defer rather than establish a competing timestamp/hash identity.
		private TelemetryEvent[]? _preparedAsyncBatch;

		/// <summary>
		/// Cached prefer-IPv4-with-IPv6-fallback delivery plan (resolved IP-literal URLs
		/// + Host header + TLS common-name override). Resolved lazily on the first flush
		/// and reused: the ingest endpoint is fixed and Cloudflare anycast DNS is stable,
		/// so re-resolving each flush would only add main-thread DNS stalls. Nullable
		/// because it is resolved lazily on the first flush, not in the constructor
		/// (a non-nullable lazy field would be CS8618 for a consumer that compiles the
		/// addon with Nullable=enable; the file-level #nullable enable makes the ? valid
		/// for a Nullable=disable consumer too).
		/// </summary>
		private EndpointAddressPlan? _plan;

		/// <summary>
		/// A RESOLUTION-FAILED passthrough (the endpoint qualifies but both families failed to
		/// resolve) leaves this false so the next flush retries resolution and a transient startup
		/// DNS failure does not permanently disable the fix.
		/// </summary>
		private bool _planCacheFinal;

		/// <summary>
		/// Opt-in positive delivery confirmation (F25). Mutable so a runtime toggle of
		/// the SDK's VerboseLogging export takes effect on the live transport instead of
		/// being frozen at the value captured when the session was initialized.
		/// </summary>
		public bool VerboseLogging { get; set; }

		/// <summary>
		/// True when the endpoint failed the transport-security check and sending is
		/// disabled (fail-closed). Exposed so the public synchronous flush can report
		/// failure up front instead of treating a disabled transport as a no-op success.
		/// </summary>
		public bool IsDisabled => _disabled;

		public long EstimateServerNowUs(long localFallbackUs)
		{
			ServerTimeObservation? observation = FreshServerTimeObservation;
			if (observation == null) return localFallbackUs;
			return GodotRuntimePolicy.EstimateServerNowUs(
				observation.ServerTimeUs,
				observation.StopwatchTicks,
				Stopwatch.GetTimestamp(),
				Stopwatch.Frequency,
				localFallbackUs);
		}

		private static long UtcNowUs() =>
			DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000L;

		private ServerTimeObservation? FreshServerTimeObservation
		{
			get
			{
				ServerTimeObservation? observation =
					Volatile.Read(ref _serverTimeObservation);
				if (observation == null) return null;
				if (GodotRuntimePolicy.IsServerTimeObservationFresh(
						observation.LocalTimeUs,
						UtcNowUs(),
						observation.StopwatchTicks,
						Stopwatch.GetTimestamp(),
						Stopwatch.Frequency))
				{
					return observation;
				}
				Interlocked.CompareExchange(
					ref _serverTimeObservation,
					null,
					observation);
				return null;
			}
		}

		private bool HasFreshServerTimeObservation =>
			FreshServerTimeObservation != null;

		private void CorrectEventTimestamps(TelemetryEvent[] events)
		{
			long serverNowUs = EstimateServerNowUs(0L);
			if (serverNowUs <= 0L) return;
			for (int i = 0; i < events.Length; i++)
			{
				events[i].TimestampUs =
					GodotRuntimePolicy.ClampTimestampUs(events[i].TimestampUs, serverNowUs);
			}
		}

		public TransportLayer(Node owner, string endpointUrl, string apiKey, string sdkVersion,
			int maxPayloadBytes, int httpTimeoutSeconds, int maxRetries, bool verboseLogging)
		{
			// Fail closed: if the endpoint fails the transport-security check, DISABLE
			// sending rather than redirecting telemetry (and the configured API key) to
			// a host the developer never configured. Matches the UE5 SDK, which drops
			// batches on a failed check. Silently substituting the default ingest host
			// could ship a self-hosted or staging deployment's player data to the vendor
			// cloud (a data-residency/privacy problem), and the SDK already prefers
			// dropping telemetry over misbehaving (see EventBuffer). HTTP is allowed
			// only for a parsed loopback host; a substring check would accept
			// "http://localhost.attacker.com" and leak the key in cleartext.
			if (!EndpointSecurity.IsEndpointTransportSecure(endpointUrl))
			{
				GD.PushError("[Framedash] Endpoint URL failed the transport-security check (must use HTTPS; HTTP is allowed only for localhost/127.0.0.1/[::1]). Telemetry is DISABLED until a secure endpoint is configured.");
				_disabled = true;
			}

			_owner = owner;
			_endpointUrl = endpointUrl;
			_apiKey = apiKey;
			_sdkVersion = sdkVersion;
			_maxPayloadBytes = maxPayloadBytes;
			VerboseLogging = verboseLogging;
			// The retry budget + per-attempt timeout bound the worst-case flush
			// wall-time (RetryPolicy.WorstCaseTotalSeconds) so a wedged endpoint cannot
			// starve later flushes via the single-flight guard (F49).
			_retryPolicy = new RetryPolicy(maxRetries, httpTimeoutSeconds: httpTimeoutSeconds);

			// HttpRequest must be a child of a Node in the tree to dispatch its
			// RequestCompleted signal. Only one request can be in flight per node; the
			// autoload guarantees a single concurrent flush, so one node is sufficient.
			_http = new HttpRequest();
			owner.AddChild(_http);
			// Short per-attempt timeout (default 10s): Godot/.NET HttpClient has no Happy
			// Eyeballs, so an IPv6 blackhole wedges the connect for the whole timeout.
			_http.Timeout = _retryPolicy.HttpTimeoutSeconds;
			_http.MaxRedirects = 0;
			// Keep processing even when the SceneTree is paused so a pause-menu flush or a
			// quit-while-paused still completes its HTTP request instead of stalling.
			_http.ProcessMode = Node.ProcessModeEnum.Always;
		}

		// Free the child HttpRequest node so re-initializing the SDK (Shutdown then
		// Initialize) does not leak HttpRequest nodes under the owner. QueueFree defers
		// to end-of-frame, so it is safe even if a send is mid-flight on this transport.
		public void Dispose()
		{
			if (GodotObject.IsInstanceValid(_http))
			{
				_http.QueueFree();
			}
		}

		private void ObserveServerTime(Variant[] result)
		{
			try
			{
				if (result.Length < 3) return;
				ObserveServerTimeHeaders(result[2].AsStringArray());
			}
			catch
			{
			}
		}

		private void ObserveServerTimeHeaders(string[] responseHeaders)
		{
			if (!GodotRuntimePolicy.TryReadServerTimestampUs(
					responseHeaders,
					out long serverTimeUs))
			{
				return;
			}
			Volatile.Write(
				ref _serverTimeObservation,
				new ServerTimeObservation(
					serverTimeUs,
					UtcNowUs(),
					Stopwatch.GetTimestamp()));
		}

		private bool EnsureServerTimeObservationBlocking(
			Stopwatch elapsed,
			int budgetMs)
		{
			if (HasFreshServerTimeObservation) return true;
			if (!BlockingRequestTarget.TryParse(_endpointUrl, out var target))
				return false;

			int bootstrapBudgetMs =
				FlushBlockingPolicy.ServerTimeBootstrapBudgetMs(budgetMs);
			if (bootstrapBudgetMs <= 0) return false;
			long observationDeadlineMs = Math.Min(
				(long)budgetMs,
				elapsed.ElapsedMilliseconds + bootstrapBudgetMs);
			var client = new HttpClient();
			try
			{
				TlsOptions? tls = target.UseTls ? TlsOptions.Client() : null;
				if (client.ConnectToHost(target.Host, target.Port, tls) != Error.Ok)
					return false;

				while (client.GetStatus() != HttpClient.Status.Connected)
				{
					HttpClient.Status status = client.GetStatus();
					if (status != HttpClient.Status.Resolving &&
						status != HttpClient.Status.Connecting)
					{
						return false;
					}
					if (client.Poll() != Error.Ok) return false;
					status = client.GetStatus();
					if (status == HttpClient.Status.Connected) break;
					long remainingMs =
						observationDeadlineMs - elapsed.ElapsedMilliseconds;
					if (remainingMs <= 0L) return false;
					OS.DelayMsec(
						RetryPolicy.NextPollDelayMs(remainingMs, BlockingPollMs));
				}

				string[] headers = { "X-SDK-Version: " + _sdkVersion };
				if (client.Request(
						HttpClient.Method.Head,
						target.Path,
						headers) != Error.Ok)
				{
					return false;
				}

				while (client.GetStatus() == HttpClient.Status.Requesting)
				{
					if (client.Poll() != Error.Ok) return false;
					if (client.GetStatus() != HttpClient.Status.Requesting)
						break;
					long remainingMs =
						observationDeadlineMs - elapsed.ElapsedMilliseconds;
					if (remainingMs <= 0L) return false;
					OS.DelayMsec(
						RetryPolicy.NextPollDelayMs(remainingMs, BlockingPollMs));
				}

				if (!client.HasResponse()) return false;
				ObserveServerTimeHeaders(client.GetResponseHeaders());
				return HasFreshServerTimeObservation;
			}
			catch
			{
				return false;
			}
			finally
			{
				client.Close();
			}
		}

	}
}
