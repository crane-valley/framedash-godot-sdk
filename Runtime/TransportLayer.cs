using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using Godot;

namespace Framedash
{
	/// <summary>
	/// Handles HTTP transport of telemetry batches to the Framedash ingest endpoint.
	/// Uses Protobuf + gzip encoding. Delegates retry decisions to <see cref="RetryPolicy"/>.
	///
	/// This is NOT a Node: it is constructed with an injected owner Node (the autoload)
	/// because Godot's HttpRequest must live in the scene tree and the retry-delay wait
	/// is driven by a SceneTreeTimer. The owner also provides the SceneTree.
	/// </summary>
	public sealed class TransportLayer : IDisposable
	{
		private readonly Node _owner;
		private readonly HttpRequest _http;
		private readonly string _endpointUrl;
		private readonly string _apiKey;
		private readonly string _sdkVersion;
		private readonly int _maxPayloadBytes;
		private readonly RetryPolicy _retryPolicy;
		private readonly bool _disabled;

		public TransportLayer(Node owner, string endpointUrl, string apiKey, string sdkVersion, int maxPayloadBytes)
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
			_retryPolicy = new RetryPolicy();

			// HttpRequest must be a child of a Node in the tree to dispatch its
			// RequestCompleted signal. Only one request can be in flight per node; the
			// autoload guarantees a single concurrent flush, so one node is sufficient.
			_http = new HttpRequest();
			owner.AddChild(_http);
			_http.Timeout = 30;     // seconds; matches the Unity SDK's 30s timeout
			_http.MaxRedirects = 0; // matches the Unity SDK (no redirects followed)
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

		/// <summary>
		/// Serialize and send a batch of events using Protobuf + gzip. Awaitable so the
		/// caller can chain flushes; the whole body is wrapped so no exception escapes
		/// (fail-safe -- game stability always wins over telemetry completeness).
		/// </summary>
		public async Task SendBatch(TelemetryEvent[] events)
		{
			try
			{
				// Fail closed: an endpoint that did not pass the security check disables
				// the transport entirely (matches the UE5 SDK dropping batches). The error
				// was already logged once at construction; stay quiet here to avoid spam.
				if (_disabled || events == null || events.Length == 0) return;

				// Chunk to the SERVER per-request caps (event count AND decoded-entry
				// count = events + all attribute/metric map entries), NOT the per-flush
				// batch threshold. The consumer rejects an over-cap batch wholesale, so a
				// drain larger than a cap (the buffer can hold up to 2x the flush batch
				// size) is split here, before serialization. A normal sub-cap drain is
				// sent as one request and chunked only by the payload-byte limit below,
				// so a stall/burst drain is not fragmented into many tiny requests.
				if (BatchPolicy.ExceedsWireCaps(events))
				{
					await SplitAndResend(events);
					return;
				}

				byte[] payload;
				try
				{
					payload = Compress(TelemetrySerializer.Serialize(events));
				}
				catch (Exception e)
				{
					GD.PushError($"[Framedash] Serialization failed: {e.Message}");
					return;
				}

				// If payload exceeds max, split batch in half and retry
				if (payload.Length > _maxPayloadBytes && events.Length > 1)
				{
					await SplitAndResend(events);
					return;
				}

				string[] headers =
				{
					"Content-Type: application/x-protobuf",
					"Content-Encoding: gzip",
					"X-API-Key: " + _apiKey,
					"X-SDK-Version: " + _sdkVersion,
				};

				for (int attempt = 0; attempt < _retryPolicy.MaxRetries; attempt++)
				{
					// RequestRaw sends the gzip bytes verbatim (RequestString would
					// re-encode as UTF-8 and corrupt the binary payload).
					Error err = _http.RequestRaw(_endpointUrl, headers, HttpClient.Method.Post, payload);
					if (err != Error.Ok)
					{
						// The request could not even be dispatched (e.g. a busy node or
						// an invalid URL). Treat it like a network error so the retry
						// policy can back off, mirroring how the Unity SDK surfaces a
						// transport failure as status 0.
						var dispatchAction = _retryPolicy.Classify(0, attempt, events.Length);
						if (dispatchAction == RetryAction.Retry)
						{
							// Last attempt: stop now without burning the backoff delay (no
								// further request follows); the loop exits to the drop log below.
								if (attempt + 1 >= _retryPolicy.MaxRetries) break;
								float dispatchDelay = _retryPolicy.GetRetryDelaySeconds(attempt);
							GD.PushWarning($"[Framedash] Retry {attempt + 1}/{_retryPolicy.MaxRetries} in {dispatchDelay:F1}s (request dispatch error {err})");
							if (!await WaitBackoff(dispatchDelay)) return;
							continue;
						}

						GD.PushWarning($"[Framedash] Send failed (request dispatch error {err}).");
						return;
					}

					// Await the RequestCompleted signal:
					//   result[0] = HttpRequest.Result enum (0 == RESULT_SUCCESS)
					//   result[1] = HTTP status code (0 when the request itself failed)
					//   result[2] = response headers
					//   result[3] = response body bytes
					Variant[] result =
						await _http.ToSignal(_http, HttpRequest.SignalName.RequestCompleted);

					long httpResult = result[0].AsInt64();
					long responseCode = result[1].AsInt64();

					// Map any transport-level failure (DNS/TLS/timeout/connection reset)
					// to status 0 before classifying, so it is treated as the retryable
					// "network error (status 0)" case exactly like the Unity SDK. A
					// non-success result with a populated status would otherwise be
					// classified on a status the request never really completed with.
					if (httpResult != (long)HttpRequest.Result.Success)
					{
						responseCode = 0;
					}

					var action = _retryPolicy.Classify(responseCode, attempt, events.Length);

					switch (action)
					{
						case RetryAction.Success:
							return;

						case RetryAction.SplitBatch:
							await SplitAndResend(events);
							return;

						case RetryAction.Fail:
							GD.PushWarning($"[Framedash] Send failed (HTTP {responseCode}): {ResponseText(result)}");
							return;

						case RetryAction.Retry:
							// Last attempt: skip the pointless backoff (no further request
								// follows); breaking the switch lets the loop exit to the drop log.
								if (attempt + 1 >= _retryPolicy.MaxRetries) break;
								float delay = _retryPolicy.GetRetryDelaySeconds(attempt);
							GD.PushWarning($"[Framedash] Retry {attempt + 1}/{_retryPolicy.MaxRetries} in {delay:F1}s (HTTP {responseCode})");
							if (!await WaitBackoff(delay)) return;
							break;
					}
				}

				GD.PushError($"[Framedash] Failed to send batch after {_retryPolicy.MaxRetries} retries. Dropping {events.Length} events.");
			}
			catch (Exception e)
			{
				// Last-resort fail-safe: never let a transport error propagate into the
				// game. Anything unexpected (signal shape, tree teardown mid-flush) is
				// logged and swallowed.
				GD.PushError($"[Framedash] Unexpected transport error: {e.Message}");
			}
		}

		// Backoff wait that stays on the Godot MAIN thread: SceneTreeTimer + ToSignal
		// resume on the main loop, unlike Task.Delay which resumes on a thread-pool
		// thread and would make the next RequestRaw an unsafe off-main engine call.
		// Returns false if the owner left the scene tree (no SceneTree) -> caller aborts.
		private async Task<bool> WaitBackoff(float seconds)
		{
			var tree = _owner.GetTree();
			if (tree == null) return false;
			// processAlways + ignoreTimeScale: the backoff follows real time even when the
			// game is paused (Paused=true) or time-scaled (Engine.TimeScale=0 for slow-mo),
			// so a retry cannot hang indefinitely while holding the single-flight guard.
			await _owner.ToSignal(
				tree.CreateTimer(seconds, processAlways: true, processInPhysics: false, ignoreTimeScale: true),
				SceneTreeTimer.SignalName.Timeout);
			return true;
		}

		private async Task SplitAndResend(TelemetryEvent[] events)
		{
			int mid = events.Length / 2;
			var firstHalf = new TelemetryEvent[mid];
			var secondHalf = new TelemetryEvent[events.Length - mid];
			Array.Copy(events, 0, firstHalf, 0, mid);
			Array.Copy(events, mid, secondHalf, 0, events.Length - mid);

			await SendBatch(firstHalf);
			await SendBatch(secondHalf);
		}

		// Decode the response body (result[3]) as UTF-8 for error logging only. Guarded
		// so a malformed signal payload cannot throw on the fail path.
		private static string ResponseText(Variant[] result)
		{
			try
			{
				if (result.Length < 4) return string.Empty;
				byte[] body = result[3].AsByteArray();
				return body == null || body.Length == 0
					? string.Empty
					: System.Text.Encoding.UTF8.GetString(body);
			}
			catch
			{
				return string.Empty;
			}
		}

		private static byte[] Compress(byte[] data)
		{
			using (var output = new MemoryStream())
			{
				using (var gzip = new GZipStream(output, CompressionMode.Compress))
				{
					gzip.Write(data, 0, data.Length);
				}
				return output.ToArray();
			}
		}
	}
}
