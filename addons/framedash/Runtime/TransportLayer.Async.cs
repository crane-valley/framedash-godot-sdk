#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Framedash
{
	public sealed partial class TransportLayer
	{
		private enum DispatchFailureOutcome
		{
			Retry,
			Fail,
			Exhausted,
		}

		/// <summary>
		/// Awaitable so the caller can chain flushes; the whole body is wrapped so no exception
		/// escapes (fail-safe -- game stability always wins over telemetry completeness). Returns the
		/// events that were NOT confirmed delivered (empty = the whole batch, including any split
		/// children, was delivered), so the caller re-buffers exactly the failed subset and never
		/// resends a delivered child (which, re-chunked on a later flush, would hash differently and
		/// dodge the consumer's array-hash dedup).
		/// </summary>
		public async Task<TelemetryEvent[]> SendBatch(TelemetryEvent[] events)
		{
			// One shared deadline for the WHOLE flush, INCLUDING any split children:
			// each child batch would otherwise get its own full retry ladder, so a
			// wedged endpoint with N chunks would hold the single-flight guard for
			// N x WorstCaseTotalSeconds. The shared stopwatch bounds the entire flush to
			// one budget regardless of split depth (F49). A failed (sub)batch appends its
			// events to `undelivered`; a delivered child appends nothing.
			var undelivered = new List<TelemetryEvent>();
			var bootstrapState = new ServerTimeBootstrapState();
			try
			{
				await SendBatchInternal(
					events,
					Stopwatch.StartNew(),
					undelivered,
					events,
					bootstrapState);
			}
			finally
			{
				Interlocked.CompareExchange(
					ref _preparedAsyncBatch,
					null,
					events);
			}
			return undelivered.ToArray();
		}

		private async Task<bool> SendBatchInternal(
			TelemetryEvent[] events,
			Stopwatch flushElapsed,
			List<TelemetryEvent> undelivered,
			TelemetryEvent[] rootEvents,
			ServerTimeBootstrapState bootstrapState)
		{
			try
			{
				// Fail closed: an endpoint that did not pass the security check disables
				// the transport entirely (matches the UE5 SDK dropping batches). The error
				// was already logged once at construction; stay quiet here to avoid spam.
				if (_disabled || events == null || events.Length == 0) return true;

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
					undelivered.AddRange(events);
					return false;
				}

				// Shared flush budget: if an earlier chunk already spent the whole budget
				// on a wedged endpoint, drop this chunk immediately rather than starting a
				// fresh ladder. Those events are lost for this flush, but later flushes'
				// buffered events still deliver -- "slow but delivering", not silent total
				// loss.
				if (!_retryPolicy.HasBudgetRemaining((float)flushElapsed.Elapsed.TotalSeconds))
				{
					GD.PushError($"[Framedash] Flush budget exhausted; dropping {events.Length} events.");
					undelivered.AddRange(events);
					return false;
				}

				EndpointAddressPlan plan = await GetDeliveryPlanAsync();
				if (bootstrapState.TryStart(
						(float)flushElapsed.Elapsed.TotalSeconds))
				{
					await EnsureServerTimeObservationAsync(
						plan,
						flushElapsed,
						bootstrapState.StartedAtSeconds);
				}
				CorrectEventTimestamps(events);

				// Chunk to the SERVER per-request caps (event count AND decoded-entry
				// count = events + all attribute/metric map entries), NOT the per-flush
				// batch threshold. The consumer rejects an over-cap batch wholesale, so a
				// drain larger than a cap (the buffer can hold up to 2x the flush batch
				// size) is split here, before serialization. A normal sub-cap drain is
				// sent as one request and chunked only by the payload-byte limit below,
				// so a stall/burst drain is not fragmented into many tiny requests.
				if (BatchPolicy.ExceedsWireCaps(events))
				{
					return await SplitAndResend(
						events,
						flushElapsed,
						undelivered,
						rootEvents,
						bootstrapState);
				}

				if (!TrySerializeBatch(events, undelivered, out byte[] payload))
					return false;

				if (payload.Length > _maxPayloadBytes && events.Length > 1)
				{
					return await SplitAndResend(
						events,
						flushElapsed,
						undelivered,
						rootEvents,
						bootstrapState);
				}
				if (ReferenceEquals(events, rootEvents))
					Volatile.Write(ref _preparedAsyncBatch, rootEvents);

				return await SendPayloadWithRetriesAsync(
					events,
					payload,
					plan,
					flushElapsed,
					undelivered,
					rootEvents,
					bootstrapState);
			}
			catch (Exception e)
			{
				// Last-resort fail-safe: never let a transport error propagate into the
				// game. Anything unexpected (signal shape, tree teardown mid-flush) is
				// logged and swallowed.
				GD.PushError($"[Framedash] Unexpected transport error: {e.Message}");
				undelivered.AddRange(events);
				return false;
			}
		}

		private static bool TrySerializeBatch(
			TelemetryEvent[] events,
			List<TelemetryEvent> undelivered,
			out byte[] payload)
		{
			try
			{
				payload = Compress(TelemetrySerializer.Serialize(events));
				return true;
			}
			catch (Exception e)
			{
				GD.PushError($"[Framedash] Serialization failed: {e.Message}");
				undelivered.AddRange(events);
				payload = Array.Empty<byte>();
				return false;
			}
		}

		private async Task<bool> SendPayloadWithRetriesAsync(
			TelemetryEvent[] events,
			byte[] payload,
			EndpointAddressPlan plan,
			Stopwatch flushElapsed,
			List<TelemetryEvent> undelivered,
			TelemetryEvent[] rootEvents,
			ServerTimeBootstrapState bootstrapState)
		{
			string[] headers = BuildRequestHeaders(plan);
			int familyIndex = 0;

			for (int attempt = 0; attempt < _retryPolicy.MaxRetries; attempt++)
			{
				float requestTimeout =
					_retryPolicy.EffectiveRequestTimeoutSeconds((float)flushElapsed.Elapsed.TotalSeconds);
				if (requestTimeout <= 0f) break;
				_http.Timeout = requestTimeout;

				Error err = _http.RequestRaw(
					plan.AttemptUrls[familyIndex],
					headers,
					HttpClient.Method.Post,
					payload);
				if (err != Error.Ok)
				{
					DispatchFailureOutcome outcome = await HandleDispatchFailureAsync(
						err,
						attempt,
						events,
						flushElapsed,
						undelivered);
					if (outcome == DispatchFailureOutcome.Retry) continue;
					if (outcome == DispatchFailureOutcome.Exhausted) break;
					return false;
				}

				(Variant[] result, long responseCode) = await ReadResponseAsync();
				RetryAction action = _retryPolicy.Classify(responseCode, attempt, events.Length);

				switch (action)
				{
					case RetryAction.Success:
						if (VerboseLogging)
							GD.Print(TransportLog.FormatFlushSuccess(events.Length, responseCode));
						return true;

					case RetryAction.SplitBatch:
						return await SplitAndResend(
							events,
							flushElapsed,
							undelivered,
							rootEvents,
							bootstrapState);

					case RetryAction.Fail:
						// Permanent rejections must not poison the retry buffer forever.
						GD.PushWarning($"[Framedash] Send permanently rejected (HTTP {responseCode}); dropping {events.Length} events: {ResponseText(result)}");
						return false;

					case RetryAction.Retry:
						if (attempt + 1 >= _retryPolicy.MaxRetries) break;
						// Only completed transport failures say anything about address-family reachability.
						if (responseCode == 0)
							familyIndex = EndpointAddressPlanner.NextFamily(familyIndex, plan.AttemptUrls.Count);
						float delay = CapToBudget(_retryPolicy.GetRetryDelaySeconds(attempt), flushElapsed);
						GD.PushWarning($"[Framedash] Retry {attempt + 1}/{_retryPolicy.MaxRetries} in {delay:F1}s (HTTP {responseCode})");
						if (!await WaitBackoff(delay))
						{
							undelivered.AddRange(events);
							return false;
						}
						break;
				}
			}

			GD.PushError($"[Framedash] Failed to send batch after {_retryPolicy.MaxRetries} retries. Dropping {events.Length} events.");
			undelivered.AddRange(events);
			return false;
		}

		private string[] BuildRequestHeaders(EndpointAddressPlan plan)
		{
			var headers = new List<string>
			{
				"Content-Type: application/x-protobuf",
				"Content-Encoding: gzip",
				"X-API-Key: " + _apiKey,
				"X-SDK-Version: " + _sdkVersion,
			};
			// The IP-literal attempt URL needs the original host for HTTP routing.
			if (!plan.IsPassthrough) headers.Add("Host: " + plan.HostHeader);
			return headers.ToArray();
		}

		private async Task<DispatchFailureOutcome> HandleDispatchFailureAsync(
			Error err,
			int attempt,
			TelemetryEvent[] events,
			Stopwatch flushElapsed,
			List<TelemetryEvent> undelivered)
		{
			if (_retryPolicy.Classify(0, attempt, events.Length) != RetryAction.Retry)
			{
				GD.PushWarning($"[Framedash] Send failed (request dispatch error {err}).");
				undelivered.AddRange(events);
				return DispatchFailureOutcome.Fail;
			}

			if (attempt + 1 >= _retryPolicy.MaxRetries)
				return DispatchFailureOutcome.Exhausted;

			// A local dispatch failure does not justify switching the address family.
			float delay = CapToBudget(_retryPolicy.GetRetryDelaySeconds(attempt), flushElapsed);
			GD.PushWarning($"[Framedash] Retry {attempt + 1}/{_retryPolicy.MaxRetries} in {delay:F1}s (request dispatch error {err})");
			if (await WaitBackoff(delay)) return DispatchFailureOutcome.Retry;

			undelivered.AddRange(events);
			return DispatchFailureOutcome.Fail;
		}

		private async Task<(Variant[] result, long responseCode)> ReadResponseAsync()
		{
			Variant[] result =
				await _http.ToSignal(_http, HttpRequest.SignalName.RequestCompleted);
			ObserveServerTime(result);

			long responseCode = result[1].AsInt64();
			// An incomplete request must follow the retryable network-error path, even
			// when Godot surfaces a stale HTTP status alongside the transport failure.
			if (result[0].AsInt64() != (long)HttpRequest.Result.Success)
				responseCode = 0;
			return (result, responseCode);
		}

		private async Task<bool> EnsureServerTimeObservationAsync(
			EndpointAddressPlan plan,
			Stopwatch flushElapsed,
			float bootstrapStartedAtSeconds)
		{
			if (HasFreshServerTimeObservation) return true;

			string[] headers = plan.IsPassthrough
				? new[] { "X-SDK-Version: " + _sdkVersion }
				: new[]
				{
					"X-SDK-Version: " + _sdkVersion,
					"Host: " + plan.HostHeader,
				};

			foreach (string targetUrl in plan.AttemptUrls)
			{
				float requestTimeout =
					_retryPolicy.EffectiveBootstrapTimeoutSeconds(
						(float)flushElapsed.Elapsed.TotalSeconds,
						bootstrapStartedAtSeconds);
				if (requestTimeout <= 0f) return false;
				_http.Timeout = requestTimeout;

				Error err = _http.RequestRaw(
					targetUrl,
					headers,
					HttpClient.Method.Head,
					Array.Empty<byte>());
				if (err != Error.Ok) continue;

				Variant[] result =
					await _http.ToSignal(_http, HttpRequest.SignalName.RequestCompleted);
				ObserveServerTime(result);
				if (HasFreshServerTimeObservation) return true;
			}

			return false;
		}

		// Poll cadence + cap for the async DNS resolve. 0.05s x 60 = ~3s: enough for a
		// cold lookup, short enough not to dominate the ~33s flush budget. On timeout we
		// do NOT freeze -- we fall back to a passthrough plan on the hostname URL for THIS
		// flush (HttpRequest then resolves it on its own worker thread, exactly the
		// pre-change behavior) and leave the cache non-final so a later flush retries.
		private const float ResolvePollSeconds = 0.05f;
		private const int ResolveMaxPolls = 60;

		// Resolution uses Godot's NATIVE async resolver queue (which runs on its own thread) polled
		// from awaited short main-thread timers, so a slow/cold DNS lookup yields to the main loop
		// instead of freezing the game thread -- the flush is already an async coroutine. A
		// resolution failure/timeout yields a passthrough plan and is NOT cached, so a transient DNS
		// failure does not permanently disable the fix.
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
				bool done4 = idv4 < 0;
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
				return true;
			}
			catch (Exception e)
			{
				GD.PushWarning($"[Framedash] DNS poll failed: {e.Message}");
				return true;
			}
		}

		private static void EraseResolve(int id)
		{
			if (id < 0) return;
			try { IP.EraseResolveItem(id); }
			catch {   }
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
		private async Task<bool> SplitAndResend(
			TelemetryEvent[] events,
			Stopwatch flushElapsed,
			List<TelemetryEvent> undelivered,
			TelemetryEvent[] rootEvents,
			ServerTimeBootstrapState bootstrapState)
		{
			if (ReferenceEquals(events, rootEvents))
				Interlocked.CompareExchange(
					ref _preparedAsyncBatch,
					null,
					rootEvents);
			int mid = events.Length / 2;
			var firstHalf = new TelemetryEvent[mid];
			var secondHalf = new TelemetryEvent[events.Length - mid];
			Array.Copy(events, 0, firstHalf, 0, mid);
			Array.Copy(events, mid, secondHalf, 0, events.Length - mid);

			bool first = await SendBatchInternal(
				firstHalf,
				flushElapsed,
				undelivered,
				rootEvents,
				bootstrapState);
			bool second = await SendBatchInternal(
				secondHalf,
				flushElapsed,
				undelivered,
				rootEvents,
				bootstrapState);
			return first && second;
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
