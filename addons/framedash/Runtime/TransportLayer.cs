#nullable enable

using System;
using System.Diagnostics;
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
		/// True once <see cref="_plan"/> is permanent and needs no rebuild: a successful
		/// resolved plan, OR a STRUCTURAL passthrough (loopback / IP-literal / non-HTTPS
		/// endpoint -- deterministic for a fixed endpoint). A RESOLUTION-FAILED passthrough
		/// (the endpoint qualifies but both families failed to resolve) leaves this false
		/// so the next flush retries resolution and a transient startup DNS failure does
		/// not permanently disable the fix.
		/// </summary>
		private bool _planCacheFinal;

		/// <summary>
		/// Opt-in positive delivery confirmation (F25). Mutable so a runtime toggle of
		/// the SDK's VerboseLogging export takes effect on the live transport instead of
		/// being frozen at the value captured when the session was initialized.
		/// </summary>
		public bool VerboseLogging { get; set; }

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
			// One shared deadline for the WHOLE flush, INCLUDING any split children:
			// each child batch would otherwise get its own full retry ladder, so a
			// wedged endpoint with N chunks would hold the single-flight guard for
			// N x WorstCaseTotalSeconds. The shared stopwatch bounds the entire flush to
			// one budget regardless of split depth (F49).
			await SendBatchInternal(events, Stopwatch.StartNew());
		}

		private async Task SendBatchInternal(TelemetryEvent[] events, Stopwatch flushElapsed)
		{
			try
			{
				// Fail closed: an endpoint that did not pass the security check disables
				// the transport entirely (matches the UE5 SDK dropping batches). The error
				// was already logged once at construction; stay quiet here to avoid spam.
				if (_disabled || events == null || events.Length == 0) return;

				// The owner Node (and its HttpRequest child) must be inside the scene tree
				// before RequestRaw, or Godot returns ERR_UNCONFIGURED and the batch is lost.
				// When no plugin autoload is registered, the Instance getter auto-creates the
				// SDK node and parents it via a DEFERRED AddChild (end-of-frame), so a
				// synchronous first Flush() in the same _Ready() runs while the owner is still
				// OUTSIDE the tree. Await the deferred entry once before issuing any request.
				// Cheap no-op on the normal (autoload / already-in-tree) path and on the
				// SplitAndResend recursion. Returns false only if the owner never enters the
				// tree within the cap (flush abandoned, fail-safe -- never hangs the guard).
				if (!await EnsureOwnerInTree(flushElapsed))
				{
					GD.PushError($"[Framedash] Owner node is not in the scene tree; dropping {events.Length} events.");
					return;
				}

				// Shared flush budget: if an earlier chunk already spent the whole budget
				// on a wedged endpoint, drop this chunk immediately rather than starting a
				// fresh ladder. Those events are lost for this flush, but later flushes'
				// buffered events still deliver -- "slow but delivering", not silent total
				// loss.
				if (!_retryPolicy.HasBudgetRemaining((float)flushElapsed.Elapsed.TotalSeconds))
				{
					GD.PushError($"[Framedash] Flush budget exhausted; dropping {events.Length} events.");
					return;
				}

				// Chunk to the SERVER per-request caps (event count AND decoded-entry
				// count = events + all attribute/metric map entries), NOT the per-flush
				// batch threshold. The consumer rejects an over-cap batch wholesale, so a
				// drain larger than a cap (the buffer can hold up to 2x the flush batch
				// size) is split here, before serialization. A normal sub-cap drain is
				// sent as one request and chunked only by the payload-byte limit below,
				// so a stall/burst drain is not fragmented into many tiny requests.
				if (BatchPolicy.ExceedsWireCaps(events))
				{
					await SplitAndResend(events, flushElapsed);
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
					await SplitAndResend(events, flushElapsed);
					return;
				}

				// Prefer-IPv4-with-IPv6-fallback: resolve the endpoint to concrete IP
				// literals (IPv4 first) and connect to those directly, so a broken-IPv6
				// network (global AAAA via Router Advertisement but no working IPv6 route)
				// cannot wedge every connect on the AAAA blackhole -- Godot's HttpRequest
				// has no Happy Eyeballs. Resolution runs on Godot's async resolver queue
				// polled from awaited main-thread timers, so a slow/cold DNS lookup never
				// freezes the game thread. A passthrough plan (loopback / IP-literal
				// endpoint, or DNS resolution failed/timed out) keeps the original behavior.
				EndpointAddressPlan plan = await GetDeliveryPlanAsync();

				string[] headers = plan.IsPassthrough
					? new[]
					{
						"Content-Type: application/x-protobuf",
						"Content-Encoding: gzip",
						"X-API-Key: " + _apiKey,
						"X-SDK-Version: " + _sdkVersion,
					}
					// Explicit Host header so Godot's HTTPClient does NOT inject its own
					// Host = the IP literal; Cloudflare Worker route-matching needs the
					// hostname. HTTPClient skips its default Host when the caller supplies
					// one (case-insensitive "Host:" match).
					: new[]
					{
						"Content-Type: application/x-protobuf",
						"Content-Encoding: gzip",
						"X-API-Key: " + _apiKey,
						"X-SDK-Version: " + _sdkVersion,
						"Host: " + plan.HostHeader,
					};

				// familyIndex walks plan.AttemptUrls (IPv4 -> IPv6). It advances only on a
				// transport-level failure (status 0), never on a real HTTP response (a
				// 5xx/429 means the server was reached, so switching family is pointless).
				int familyIndex = 0;

				for (int attempt = 0; attempt < _retryPolicy.MaxRetries; attempt++)
				{
					// Cap this request's timeout to the remaining shared flush budget so a
					// request started near the deadline cannot overshoot it by a full
					// HttpTimeoutSeconds. Zero budget -> stop and exit to the drop log.
					float requestTimeout =
						_retryPolicy.EffectiveRequestTimeoutSeconds((float)flushElapsed.Elapsed.TotalSeconds);
					if (requestTimeout <= 0f) break;
					_http.Timeout = requestTimeout;

					// Connect to the currently-selected family's IP literal (or the
					// original URL for a passthrough plan). SNI + certificate verification
					// still target the FQDN via the TLS common-name override set in
					// GetDeliveryPlan, and the Host header above keeps the HTTP routing name.
					string targetUrl = plan.AttemptUrls[familyIndex];

					// RequestRaw sends the gzip bytes verbatim (RequestString would
					// re-encode as UTF-8 and corrupt the binary payload).
					Error err = _http.RequestRaw(targetUrl, headers, HttpClient.Method.Post, payload);
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
								// Do NOT switch family here: a RequestRaw dispatch failure is a
								// LOCAL fault (busy request node / malformed URL), not a
								// transport-level connectivity failure, so it says nothing about
								// this family's reachability. Retry the same family.
								float dispatchDelay = CapToBudget(_retryPolicy.GetRetryDelaySeconds(attempt), flushElapsed);
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
							// Opt-in positive delivery confirmation (F25): off by
							// default so it never spams a shipping game; first-time
							// integrators flip VerboseLogging to confirm delivery.
							if (VerboseLogging)
								GD.Print(TransportLog.FormatFlushSuccess(events.Length, responseCode));
							return;

						case RetryAction.SplitBatch:
							await SplitAndResend(events, flushElapsed);
							return;

						case RetryAction.Fail:
							GD.PushWarning($"[Framedash] Send failed (HTTP {responseCode}): {ResponseText(result)}");
							return;

						case RetryAction.Retry:
							// Last attempt: skip the pointless backoff (no further request
								// follows); breaking the switch lets the loop exit to the drop log.
								if (attempt + 1 >= _retryPolicy.MaxRetries) break;
								// A transport-level failure (status 0: DNS/TLS/timeout/reset)
								// means this address family did not connect -- TOGGLE to the
								// other family for the next attempt (IPv4 <-> IPv6, wrapping),
								// so an IPv6-only network delivers over IPv6 AND a broken-IPv6
								// network can return to the working IPv4 after a transient
								// glitch instead of wedging on the IPv6 blackhole. A real HTTP
								// status (5xx/429) means the server was reached, so keep the
								// same family.
								if (responseCode == 0)
									familyIndex = EndpointAddressPlanner.NextFamily(familyIndex, plan.AttemptUrls.Count);
								// Cap the backoff to the remaining shared budget so a wait
								// cannot push the flush past its deadline either.
								float delay = CapToBudget(_retryPolicy.GetRetryDelaySeconds(attempt), flushElapsed);
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

		// Poll cadence + cap for the async DNS resolve. 0.05s x 60 = ~3s: enough for a
		// cold lookup, short enough not to dominate the ~33s flush budget. On timeout we
		// do NOT freeze -- we fall back to a passthrough plan on the hostname URL for THIS
		// flush (HttpRequest then resolves it on its own worker thread, exactly the
		// pre-change behavior) and leave the cache non-final so a later flush retries.
		private const float ResolvePollSeconds = 0.05f;
		private const int ResolveMaxPolls = 60;

		// Resolve (once, lazily) the endpoint to concrete IP literals and build the
		// prefer-IPv4-with-IPv6-fallback delivery plan, then cache it (fixed endpoint +
		// stable anycast DNS). Resolution uses Godot's NATIVE async resolver queue (which
		// runs on its own thread) polled from awaited short main-thread timers, so a
		// slow/cold DNS lookup yields to the main loop instead of freezing the game
		// thread -- the flush is already an async coroutine. A resolution failure/timeout
		// yields a passthrough plan and is NOT cached, so a transient DNS failure does not
		// permanently disable the fix.
		private async Task<EndpointAddressPlan> GetDeliveryPlanAsync()
		{
			if (_planCacheFinal && _plan != null) return _plan;

			if (!EndpointAddressPlanner.ShouldForceAddressFamily(_endpointUrl))
			{
				// Structural passthrough (loopback / IP-literal / non-HTTPS): deterministic
				// for a fixed endpoint, so build and cache it once -- no per-flush re-Build.
				_plan = EndpointAddressPlanner.Build(_endpointUrl, null, null);
				_planCacheFinal = true;
				return _plan;
			}

			string host = new Uri(_endpointUrl).Host;
			var (ipv4, ipv6) = await ResolveBothAsync(host);
			_plan = EndpointAddressPlanner.Build(_endpointUrl, ipv4, ipv6);

			if (!_plan.IsPassthrough)
			{
				// Connect to a pinned IP literal but keep SNI + certificate verification
				// as the real FQDN: common_name_override drives mbedtls_ssl_set_hostname,
				// which sets BOTH the SNI extension and the certificate CN check.
				// trusted_chain stays null = default CA bundle, so validation is unchanged
				// except for the hostname it validates against. Set once (plan is cached).
				_http.SetTlsOptions(TlsOptions.Client(null, _plan.CommonName));
				_planCacheFinal = true;
			}
			// else: resolution failed/timed out -- passthrough on the hostname URL for this
			// flush; leave the cache non-final so a later flush retries DNS resolution.
			return _plan;
		}

		// Queue BOTH families on Godot's async resolver at once and poll them together, so
		// the total wait is bounded by ONE poll cap rather than doubled. Returns the first
		// address of each family (empty when a family did not resolve, errored, or the cap
		// elapsed). Stays on the MAIN thread and never throws (fail-safe).
		private async Task<(string ipv4, string ipv6)> ResolveBothAsync(string host)
		{
			int idv4 = QueueResolve(host, IP.Type.Ipv4);
			int idv6 = QueueResolve(host, IP.Type.Ipv6);
			try
			{
				string ipv4 = string.Empty, ipv6 = string.Empty;
				bool done4 = idv4 < 0; // queue failed -> treat as resolved-empty
				bool done6 = idv6 < 0;

				for (int i = 0; i < ResolveMaxPolls && (!done4 || !done6); i++)
				{
					// Yield to the main loop; resumes on a later frame WITHOUT freezing. If
					// there is no running main loop at all, stop and use whatever resolved so far.
					if (!await WaitBackoff(ResolvePollSeconds)) break;
					if (!done4) done4 = TryReadResolve(idv4, out ipv4);
					if (!done6) done6 = TryReadResolve(idv6, out ipv6);
				}
				return (ipv4, ipv6);
			}
			finally
			{
				EraseResolve(idv4);
				EraseResolve(idv6);
			}
		}

		// Queue one async resolve; returns the queue id, or a negative id on failure.
		private static int QueueResolve(string host, IP.Type family)
		{
			try
			{
				return IP.ResolveHostnameQueueItem(host, family);
			}
			catch (Exception e)
			{
				GD.PushWarning($"[Framedash] DNS queue failed ({family}): {e.Message}");
				return -1;
			}
		}

		// Poll one queued resolve. Returns true once the item reached a TERMINAL state
		// (Done -> first address in <paramref name="address"/>; Error/None -> empty),
		// false while still Waiting. Any engine error is treated as terminal-empty.
		private static bool TryReadResolve(int id, out string address)
		{
			address = string.Empty;
			if (id < 0) return true;
			try
			{
				IP.ResolverStatus status = IP.GetResolveItemStatus(id);
				if (status == IP.ResolverStatus.Waiting) return false;
				if (status == IP.ResolverStatus.Done)
					address = IP.GetResolveItemAddress(id) ?? string.Empty;
				return true; // Done / Error / None are all terminal
			}
			catch (Exception e)
			{
				GD.PushWarning($"[Framedash] DNS poll failed: {e.Message}");
				return true;
			}
		}

		// Free a resolver queue slot; best-effort (ignore any error).
		private static void EraseResolve(int id)
		{
			if (id < 0) return;
			try { IP.EraseResolveItem(id); }
			catch { /* best-effort cleanup */ }
		}

		// Clamp a backoff delay to the remaining shared flush budget so a wait cannot
		// push the flush past its deadline. Never negative.
		private float CapToBudget(float delay, Stopwatch flushElapsed)
		{
			float remaining = _retryPolicy.RemainingBudgetSeconds((float)flushElapsed.Elapsed.TotalSeconds);
			return delay < remaining ? delay : remaining;
		}

		// End-of-frame cap for awaiting the owner Node's tree entry before the first send.
		// The auto-created node's deferred AddChild runs at the end of the current frame, so
		// the first WaitBackoff yield is normally enough; the cap only bounds a pathological
		// owner that never enters the tree so a flush cannot hang the single-flight guard
		// forever. Reuses the DNS-resolve poll cadence (ResolvePollSeconds).
		private const int TreeEntryMaxPolls = 60;

		// Await the owner Node's scene-tree entry before the first send. When the SDK node was
		// auto-created by the Instance getter (no plugin autoload registered), its AddChild is
		// DEFERRED to end-of-frame, so a synchronous first Flush() in the same _Ready() runs
		// while the owner -- and its HttpRequest child -- are still OUTSIDE the tree, where
		// RequestRaw returns ERR_UNCONFIGURED and the batch is dropped. Poll until the deferred
		// entry lands (event-free, reusing the out-of-tree-safe WaitBackoff so the yield resumes
		// on the MAIN thread). Already-in-tree (autoload / recursion) returns immediately with no
		// await -- the in-tree fast path stays zero-cost. Each poll is clamped to the remaining
		// shared flush budget (CapToBudget) so a tight config (small HttpTimeoutSeconds/MaxRetries)
		// cannot let the single-flight guard overrun, and a SplitAndResend chunk that finds the
		// owner gone cannot re-pay a fresh poll ladder beyond the flush deadline. Returns false if
		// the owner never entered the tree within the cap OR the budget is exhausted.
		private async Task<bool> EnsureOwnerInTree(Stopwatch flushElapsed)
		{
			// The owner may have been freed (scene change / app exit) before or during the
			// wait; IsInsideTree() on a freed object throws, so re-validate at the start and
			// after every await before touching it. One cheap native check on the rare
			// batch-send path -- nothing per-frame.
			if (!GodotObject.IsInstanceValid(_owner)) return false;
			if (_owner.IsInsideTree()) return true;
			for (int i = 0; i < TreeEntryMaxPolls; i++)
			{
				float pollWait = CapToBudget(ResolvePollSeconds, flushElapsed);
				if (pollWait <= 0f) return false;
				if (!await WaitBackoff(pollWait)) return false;
				if (!GodotObject.IsInstanceValid(_owner)) return false;
				if (_owner.IsInsideTree()) return true;
			}
			// Loop exhausted without the owner entering the tree.
			return false;
		}

		// Backoff wait that stays on the Godot MAIN thread: SceneTreeTimer + ToSignal
		// resume on the main loop, unlike Task.Delay which resumes on a thread-pool
		// thread and would make the next RequestRaw an unsafe off-main engine call.
		// Prefer the owner's own tree, but fall back to the main-loop SceneTree when the
		// owner is not (yet) inside the tree: the auto-create fallback (no plugin autoload)
		// parents the SDK node via a DEFERRED AddChild, so during the same-frame first flush
		// GetTree() is null -- bailing there would silently drop events, and the main-loop
		// tree still drives a main-thread SceneTreeTimer. Returns false only when there is no
		// running main loop at all (tool/headless teardown) -> caller aborts.
		private async Task<bool> WaitBackoff(float seconds)
		{
			// Guard against a freed owner (scene change / exit) before dereferencing it:
			// GetTree()/ToSignal() on a disposed object would throw.
			if (!GodotObject.IsInstanceValid(_owner)) return false;
			// Only call GetTree() when the node is actually in the tree. Node::get_tree()
			// pushes a native "Parameter data.tree is null" error to the console when the
			// node is OUTSIDE the tree before returning null -- which is exactly the
			// auto-create first-flush case (deferred AddChild, owner not yet in tree). The
			// null-coalescing fallback recovered functionally, but the spurious error was
			// still printed during the documented quickstart. Skip GetTree() off-tree and
			// go straight to the main-loop SceneTree, which drives the same main-thread
			// SceneTreeTimer.
			var tree = (_owner.IsInsideTree() ? _owner.GetTree() : null)
				?? (Engine.GetMainLoop() as SceneTree);
			if (tree == null) return false;
			// processAlways + ignoreTimeScale: the backoff follows real time even when the
			// game is paused (Paused=true) or time-scaled (Engine.TimeScale=0 for slow-mo),
			// so a retry cannot hang indefinitely while holding the single-flight guard.
			await _owner.ToSignal(
				tree.CreateTimer(seconds, processAlways: true, processInPhysics: false, ignoreTimeScale: true),
				SceneTreeTimer.SignalName.Timeout);
			return true;
		}

		// Split a batch in half and resend both children under the SAME shared flush
		// deadline (flushElapsed), so the total single-flight window stays bounded by
		// one RetryPolicy.WorstCaseTotalSeconds budget regardless of split depth.
		private async Task SplitAndResend(TelemetryEvent[] events, Stopwatch flushElapsed)
		{
			int mid = events.Length / 2;
			var firstHalf = new TelemetryEvent[mid];
			var secondHalf = new TelemetryEvent[events.Length - mid];
			Array.Copy(events, 0, firstHalf, 0, mid);
			Array.Copy(events, mid, secondHalf, 0, events.Length - mid);

			await SendBatchInternal(firstHalf, flushElapsed);
			await SendBatchInternal(secondHalf, flushElapsed);
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

		// ---- Synchronous shutdown drain --------------------------------------------
		//
		// The normal flush path (SendBatch) is async and dispatches through the
		// HttpRequest scene-tree node. During synchronous tree teardown (_ExitTree on
		// quit) that path can never complete: the awaited continuation resumes only on a
		// later main-loop frame, but the owner node -- and its HttpRequest child -- have
		// already left the tree, so the batch is dropped (RequestRaw returns
		// ERR_UNCONFIGURED / the owner is no longer in the tree). Godot's low-level
		// HttpClient needs no scene-tree node, so it can POST from a blocking call inside
		// the teardown. The whole drain is bounded by a small wall-clock budget so a
		// wedged endpoint can never hang application quit. The async runtime path above
		// is intentionally left unchanged.

		// Poll cadence for the blocking HttpClient state machine. 10ms keeps quit
		// responsive while not busy-spinning the CPU during the (bounded) drain.
		private const int BlockingPollMs = 10;

		/// <summary>
		/// Best-effort SYNCHRONOUS drain used only on shutdown/teardown, when the async
		/// HttpRequest path cannot complete. Each envelope is POSTed as its OWN independent
		/// blocking request (never concatenated): the consumer deduplicates by hashing the
		/// full ordered event array (apps/consumer message-helpers.hashEventBatch), so an
		/// envelope re-sent with its original event array keeps the same dedup token and is
		/// dropped if it already reached ingest -- merging it with other events would change
		/// the hash and duplicate those events. All envelopes SHARE one wall-clock budget
		/// (<paramref name="budgetMs"/>) so quit is never hung; on timeout later envelopes
		/// are dropped gracefully. A failed envelope does NOT skip the rest. Each envelope
		/// is still split internally (wire caps, then estimated size, then payload bytes)
		/// so no single serialize+gzip or request is unbounded. Returns true only when every
		/// envelope was accepted (HTTP 2xx). Never throws (fail-safe).
		/// </summary>
		public bool SendEnvelopesBlocking(TelemetryEvent[][] envelopes, int budgetMs)
		{
			// An empty or disabled drain is a no-op success: there is nothing to lose.
			if (_disabled || envelopes == null || envelopes.Length == 0) return true;
			var elapsed = Stopwatch.StartNew();
			try
			{
				bool ok = true;
				for (int i = 0; i < envelopes.Length; i++)
				{
					TelemetryEvent[] batch = envelopes[i];
					if (batch == null || batch.Length == 0) continue;
					// Do NOT short-circuit on failure: a failed (possibly-redundant) in-flight
					// resend must not skip a later, guaranteed-undelivered envelope.
					ok = SendChunkBlocking(batch, elapsed, budgetMs) && ok;
				}
				return ok;
			}
			catch (Exception e)
			{
				GD.PushError($"[Framedash] Shutdown drain failed: {e.Message}");
				return false;
			}
		}

		private bool SendChunkBlocking(TelemetryEvent[] events, Stopwatch elapsed, int budgetMs)
		{
			if (events.Length == 0) return true;
			if (elapsed.ElapsedMilliseconds >= budgetMs)
			{
				GD.PushError($"[Framedash] Shutdown drain budget exhausted; dropping {events.Length} events.");
				return false;
			}

			// Same server per-request caps as the async path: an over-cap batch is
			// rejected wholesale, so split before serializing.
			if (BatchPolicy.ExceedsWireCaps(events))
				return SplitAndSendBlocking(events, elapsed, budgetMs);

			// Bound the CPU of the (uninterruptible) serialize+gzip: an attribute-heavy
			// legal backlog can be tens of MB and would blow the drain budget in one shot
			// before any budget re-check. Split by estimated size FIRST so each serialize
			// stays within MaxBlockingChunkBytes.
			if (BatchPolicy.ExceedsBlockingChunkBytes(events))
				return SplitAndSendBlocking(events, elapsed, budgetMs);

			byte[] payload;
			try
			{
				payload = Compress(TelemetrySerializer.Serialize(events));
			}
			catch (Exception e)
			{
				GD.PushError($"[Framedash] Serialization failed during shutdown drain: {e.Message}");
				return false;
			}

			// Re-check the budget AFTER serialize+gzip: those run inside the shared window
			// but are not themselves interruptible, so a large backlog could have consumed
			// the budget here. Bailing now keeps the total bounded (no network time is
			// added on top of an already-overrun serialize).
			if (elapsed.ElapsedMilliseconds >= budgetMs)
			{
				GD.PushError($"[Framedash] Shutdown drain budget exhausted; dropping {events.Length} events.");
				return false;
			}

			if (payload.Length > _maxPayloadBytes && events.Length > 1)
				return SplitAndSendBlocking(events, elapsed, budgetMs);

			return PostBlocking(payload, events.Length, elapsed, budgetMs);
		}

		// Both halves share the SAME elapsed stopwatch / budget so the whole drain stays
		// within one bound regardless of split depth (mirrors SplitAndResend).
		private bool SplitAndSendBlocking(TelemetryEvent[] events, Stopwatch elapsed, int budgetMs)
		{
			int mid = events.Length / 2;
			var first = new TelemetryEvent[mid];
			var second = new TelemetryEvent[events.Length - mid];
			Array.Copy(events, 0, first, 0, mid);
			Array.Copy(events, mid, second, 0, events.Length - mid);
			bool okFirst = SendChunkBlocking(first, elapsed, budgetMs);
			bool okSecond = SendChunkBlocking(second, elapsed, budgetMs);
			return okFirst && okSecond;
		}

		// One blocking POST via HttpClient. Drives the connect -> request -> response
		// state machine by hand (Poll + a short sleep), re-checking the shared budget at
		// every step so a stalled connect / handshake / response cannot overrun the quit
		// budget. Connects by hostname and lets HttpClient resolve: the IPv4-preference
		// plan of the async path is a per-flush latency optimization not worth
		// duplicating on the one-shot teardown drain, and the budget already caps a slow
		// resolve. Returns true only on a 2xx response.
		private bool PostBlocking(byte[] payload, int eventCount, Stopwatch elapsed, int budgetMs)
		{
			if (!BlockingRequestTarget.TryParse(_endpointUrl, out var target))
			{
				GD.PushError($"[Framedash] Shutdown drain could not parse endpoint; dropping {eventCount} events.");
				return false;
			}

			var client = new HttpClient();
			try
			{
				TlsOptions? tls = target.UseTls ? TlsOptions.Client() : null;
				if (client.ConnectToHost(target.Host, target.Port, tls) != Error.Ok) return false;

				// Wait for the connection (Resolving -> Connecting -> Connected). Any
				// other status is a terminal connect/handshake failure.
				while (true)
				{
					HttpClient.Status status = client.GetStatus();
					if (status == HttpClient.Status.Connected) break;
					if (status != HttpClient.Status.Resolving && status != HttpClient.Status.Connecting)
						return false;
					if (!PollWithinBudget(client, elapsed, budgetMs)) return false;
				}

				// Connecting by hostname means HttpClient injects the correct Host itself,
				// so no explicit Host header / TLS common-name override is needed here.
				string[] headers =
				{
					"Content-Type: application/x-protobuf",
					"Content-Encoding: gzip",
					"X-API-Key: " + _apiKey,
					"X-SDK-Version: " + _sdkVersion,
				};
				if (client.RequestRaw(HttpClient.Method.Post, target.Path, headers, payload) != Error.Ok)
					return false;

				while (client.GetStatus() == HttpClient.Status.Requesting)
				{
					if (!PollWithinBudget(client, elapsed, budgetMs)) return false;
				}

				int code = client.GetResponseCode();
				bool ok = code >= 200 && code < 300;
				if (ok)
				{
					if (VerboseLogging) GD.Print(TransportLog.FormatFlushSuccess(eventCount, code));
				}
				else
				{
					GD.PushWarning($"[Framedash] Shutdown drain send failed (HTTP {code}); dropping {eventCount} events.");
				}
				return ok;
			}
			finally
			{
				client.Close();
			}
		}

		// Advance the HttpClient state machine once and sleep one poll interval unless the
		// shared budget is already spent. Returns false when the budget is exhausted so
		// the caller aborts. OS.DelayMsec blocks the calling thread -- acceptable ONLY
		// because this path runs during teardown, never per frame.
		private static bool PollWithinBudget(HttpClient client, Stopwatch elapsed, int budgetMs)
		{
			client.Poll();
			if (elapsed.ElapsedMilliseconds >= budgetMs) return false;
			OS.DelayMsec(BlockingPollMs);
			return true;
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
