#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Godot;

namespace Framedash
{
	public sealed partial class TransportLayer
	{
		// The normal flush path (SendBatch) is async and dispatches through the HttpRequest
		// scene-tree node. During synchronous tree teardown (_ExitTree on quit) that path can never
		// complete: the awaited continuation resumes only on a later main-loop frame, but the owner
		// node -- and its HttpRequest child -- have already left the tree, so the batch is dropped
		// (RequestRaw returns ERR_UNCONFIGURED / the owner is no longer in the tree). Godot's
		// low-level HttpClient needs no scene-tree node, so it can POST from a blocking call inside
		// the teardown. The whole drain is bounded by a small wall-clock budget so a wedged endpoint
		// can never hang application quit. The async runtime path above is intentionally left
		// unchanged.

		// Poll cadence for the blocking HttpClient state machine. 10ms keeps quit
		// responsive while not busy-spinning the CPU during the (bounded) drain. Each
		// sleep is capped to the remaining budget (RetryPolicy.NextPollDelayMs) and
		// OS.DelayMsec blocks the calling thread -- acceptable ONLY because this path
		// runs on the blocking flush/teardown drain, never per frame.
		private const int BlockingPollMs = 10;
		/// <summary>
		/// Async HttpRequest continuations cannot complete during tree teardown, so shutdown needs a
		/// blocking drain. Envelopes must retain their original event arrays because the consumer
		/// hashes each array for deduplication; merging envelopes would duplicate an already
		/// delivered batch.
		///
		/// A shared wall-clock budget prevents application quit from hanging. Per-envelope splitting
		/// still bounds serialization, compression, and request size.
		/// </summary>
		public bool SendEnvelopesBlocking(TelemetryEvent[][] envelopes, int budgetMs, int unreshapedIndex = -1)
		{
			// Aggregate all-or-nothing wrapper over the per-envelope path. The shutdown
			// drain only needs one bool; the public FlushBlocking uses the undelivered-events
			// variant so it can re-enqueue exactly the events that were not confirmed.
			// unreshapedIndex marks the in-flight envelope non-splittable (see the detailed
			// variant) so a partially-delivered parked batch's children are never reshaped.
			// A permanent rejection also fails the result even though its events are dropped
			// (never requeued), so a poison batch is surfaced, not silently reported success.
			// budgetOverran is deliberately IGNORED here: this aggregate serves the terminal
			// shutdown drain, whose return value no caller uses to retry -- a late-confirmed
			// batch on quit is simply delivered, and there is no public result to keep honest.
			TelemetryEvent[][] undelivered = SendEnvelopesBlockingUndeliveredInternal(
				envelopes,
				budgetMs,
				unreshapedIndex,
				out bool anyPermanentDrop,
				out bool _,
				asyncCanResume: false);
			if (anyPermanentDrop) return false;
			for (int i = 0; i < undelivered.Length; i++)
				if (undelivered[i] != null && undelivered[i].Length > 0) return false;
			return true;
		}

		/// <summary>
		/// Requeueing only failed chunks avoids resending delivered children: different chunking on a
		/// later flush would change their hashes and bypass consumer deduplication. An unexpected
		/// failure preserves the envelope so the caller does not lose its events.
		///
		/// An in-flight envelope must retain its serialized shape and timestamps. Sending it before
		/// the async path establishes those bytes could double-insert when both requests succeed, so
		/// a live flush defers it. Terminal shutdown cannot defer because the async continuation will
		/// never resume.
		///
		/// Permanent rejections are dropped because retries cannot succeed and would poison the
		/// buffer, but still fail the aggregate result so the loss stays visible. Late confirmations
		/// remain acknowledged to prevent duplicates while reporting failure to honor the caller's
		/// requested deadline.
		/// </summary>
		public TelemetryEvent[][] SendEnvelopesBlockingUndelivered(
			TelemetryEvent[][] envelopes,
			int budgetMs,
			int unreshapedIndex,
			out bool anyPermanentDrop,
			out bool budgetOverran)
			=> SendEnvelopesBlockingUndeliveredInternal(
				envelopes,
				budgetMs,
				unreshapedIndex,
				out anyPermanentDrop,
				out budgetOverran,
				asyncCanResume: true);

		private TelemetryEvent[][] SendEnvelopesBlockingUndeliveredInternal(
			TelemetryEvent[][] envelopes,
			int budgetMs,
			int unreshapedIndex,
			out bool anyPermanentDrop,
			out bool budgetOverran,
			bool asyncCanResume)
		{
			anyPermanentDrop = false;
			budgetOverran = false;
			if (envelopes == null || envelopes.Length == 0) return System.Array.Empty<TelemetryEvent[]>();
			var result = new TelemetryEvent[envelopes.Length][];
			if (_disabled)
			{
				for (int i = 0; i < result.Length; i++) result[i] = System.Array.Empty<TelemetryEvent>();
				return result;
			}
			var elapsed = Stopwatch.StartNew();
			EnsureServerTimeObservationBlocking(elapsed, budgetMs);
			// Permanently-rejected events across ALL envelopes: tracked only to set the
			// anyPermanentDrop result flag; they are never requeued.
			var permanentlyDropped = new List<TelemetryEvent>();
			for (int i = 0; i < envelopes.Length; i++)
			{
				TelemetryEvent[] batch = envelopes[i];
				if (batch == null || batch.Length == 0) { result[i] = System.Array.Empty<TelemetryEvent>(); continue; }
				bool isUnreshaped = i == unreshapedIndex;
				bool isPrepared = ReferenceEquals(
					Volatile.Read(ref _preparedAsyncBatch),
					batch);
				if (FlushBlockingPolicy.ShouldDeferUnpreparedEnvelope(
						isUnreshaped,
						isPrepared,
						asyncCanResume))
				{
					result[i] = batch;
					continue;
				}
				if (FlushBlockingPolicy.ShouldCorrectEnvelopeTimestamps(
						isUnreshaped,
						isPrepared))
				{
					CorrectEventTimestamps(batch);
				}
				var undelivered = new List<TelemetryEvent>();
				try
				{
					// Do NOT short-circuit across envelopes on failure: a failed (possibly-
					// redundant) in-flight resend must not skip a later, guaranteed-undelivered
					// envelope. SendChunkBlocking appends transient failures to `undelivered`
					// (requeue) and permanent rejections to `permanentlyDropped` (dropped).
					// The in-flight envelope is NON-splittable: it is sent in its identical
					// original shape or deferred (never reshaped), so it stays byte-identical to
					// the parked async flush's send and the consumer dedups instead of double-
					// inserting. Every other envelope is freely splittable.
					bool splittable = i != unreshapedIndex;
					SendChunkBlocking(batch, elapsed, budgetMs, undelivered, permanentlyDropped, ref budgetOverran, splittable);
				}
				catch (Exception e)
				{
					GD.PushError($"[Framedash] Blocking drain failed: {e.Message}");
					// Conservative fail-safe: an unexpected error is treated as transient, so
					// keep the whole envelope for requeue rather than dropping it.
					undelivered.Clear();
					undelivered.AddRange(batch);
				}
				result[i] = undelivered.ToArray();
			}
			anyPermanentDrop = permanentlyDropped.Count > 0;
			return result;
		}

		// TRANSIENT failures (timeout / 5xx / 429 / network / budget / deferral) append their events
		// to `undelivered` (requeue); PERMANENT rejections (3xx / non-retryable 4xx) append to
		// `permanentlyDropped` (dropped, never requeued -- re-sending can never succeed, so requeuing
		// would poison the buffer forever). Nothing is appended on success. Returns true iff every
		// event in `events` was accepted (HTTP 2xx). A split chunk records its children
		// independently, so a partially-delivered envelope yields only the failed children.
		//
		// `splittable` is false for the seized in-flight envelope: a parked async flush still owns
		// that batch and resends the SAME original array, so blocking mode must send it in the
		// IDENTICAL shape or not at all -- any reshape (wire-cap, payload, or the blocking-only
		// chunk-bytes split) would give the delivered chunks a different batch hash than the async
		// send and double-insert if both succeed. So when the batch does not fit as one request it is
		// DEFERRED (reported undelivered, never reshaped): the still-live async flush (or, on
		// shutdown where no async competes, the splittable drain) delivers it instead. The buffered
		// envelope is splittable -- it has no async owner, its delivered chunks are acked and its
		// failed chunks never reached ingest, so its shape is irrelevant to dedup.
		private bool SendChunkBlocking(TelemetryEvent[] events, Stopwatch elapsed, int budgetMs, List<TelemetryEvent> undelivered, List<TelemetryEvent> permanentlyDropped, ref bool budgetOverran, bool splittable = true)
		{
			if (events.Length == 0) return true;
			if (elapsed.ElapsedMilliseconds >= budgetMs)
			{
				GD.PushError($"[Framedash] Shutdown drain budget exhausted; dropping {events.Length} events.");
				undelivered.AddRange(events);
				return false;
			}

			// Same server per-request caps as the async path: an over-cap batch is
			// rejected wholesale, so split before serializing (or defer if not splittable).
			if (BatchPolicy.ExceedsWireCaps(events))
			{
				if (!splittable) { undelivered.AddRange(events); return false; }
				return SplitAndSendBlocking(events, elapsed, budgetMs, undelivered, permanentlyDropped, ref budgetOverran, splittable);
			}

			// Bound the CPU of the (uninterruptible) serialize+gzip: an attribute-heavy
			// legal backlog can be tens of MB and would blow the drain budget in one shot
			// before any budget re-check. Split by estimated size FIRST so each serialize
			// stays within MaxBlockingChunkBytes (or defer, unserialized, if not splittable --
			// a batch too big to serialize in one shot cannot be sent as one request either).
			if (BatchPolicy.ExceedsBlockingChunkBytes(events))
			{
				if (!splittable) { undelivered.AddRange(events); return false; }
				return SplitAndSendBlocking(events, elapsed, budgetMs, undelivered, permanentlyDropped, ref budgetOverran, splittable);
			}

			byte[] payload;
			try
			{
				payload = Compress(TelemetrySerializer.Serialize(events));
			}
			catch (Exception e)
			{
				GD.PushError($"[Framedash] Serialization failed during shutdown drain: {e.Message}");
				undelivered.AddRange(events);
				return false;
			}

			// Re-check the budget AFTER serialize+gzip: those run inside the shared window
			// but are not themselves interruptible, so a large backlog could have consumed
			// the budget here. Bailing now keeps the total bounded (no network time is
			// added on top of an already-overrun serialize).
			if (elapsed.ElapsedMilliseconds >= budgetMs)
			{
				GD.PushError($"[Framedash] Shutdown drain budget exhausted; dropping {events.Length} events.");
				undelivered.AddRange(events);
				return false;
			}

			if (payload.Length > _maxPayloadBytes && events.Length > 1)
			{
				if (!splittable) { undelivered.AddRange(events); return false; }
				return SplitAndSendBlocking(events, elapsed, budgetMs, undelivered, permanentlyDropped, ref budgetOverran, splittable);
			}

			BlockingSendOutcome outcome = PostBlocking(payload, events.Length, elapsed, budgetMs, ref budgetOverran);
			if (outcome == BlockingSendOutcome.Delivered) return true;
			// Server said the payload is too large (multi-event 413): halve and resend under
			// the same shared budget, mirroring the async RetryAction.SplitBatch -- otherwise
			// a MaxPayloadBytes configured above the real endpoint limit would requeue the
			// same oversized batch and deterministically fail forever even though its halves
			// deliver. The NON-splittable in-flight envelope keeps its round-5 whole-or-defer
			// contract: it is deferred to `undelivered` (retained, never reshaped) instead.
			if (outcome == BlockingSendOutcome.SplitPayload)
			{
				if (!splittable) { undelivered.AddRange(events); return false; }
				return SplitAndSendBlocking(events, elapsed, budgetMs, undelivered, permanentlyDropped, ref budgetOverran, splittable);
			}
			(outcome == BlockingSendOutcome.PermanentDrop ? permanentlyDropped : undelivered).AddRange(events);
			return false;
		}

		// Both halves share the SAME elapsed stopwatch / budget so the whole drain stays
		// within one bound regardless of split depth (mirrors SplitAndResend). Each half
		// records its own undelivered/permanently-dropped events, so a mixed success/failure
		// split contributes only the failed half's events. Only reached for a splittable
		// envelope; children stay splittable.
		private bool SplitAndSendBlocking(TelemetryEvent[] events, Stopwatch elapsed, int budgetMs, List<TelemetryEvent> undelivered, List<TelemetryEvent> permanentlyDropped, ref bool budgetOverran, bool splittable = true)
		{
			int mid = events.Length / 2;
			var first = new TelemetryEvent[mid];
			var second = new TelemetryEvent[events.Length - mid];
			Array.Copy(events, 0, first, 0, mid);
			Array.Copy(events, mid, second, 0, events.Length - mid);
			bool okFirst = SendChunkBlocking(first, elapsed, budgetMs, undelivered, permanentlyDropped, ref budgetOverran, splittable);
			bool okSecond = SendChunkBlocking(second, elapsed, budgetMs, undelivered, permanentlyDropped, ref budgetOverran, splittable);
			return okFirst && okSecond;
		}

		// The outcome of one blocking POST, so the caller can DROP a permanent rejection
		// (never requeue it), SPLIT a payload the server rejected as too large, or keep a
		// transient failure for requeue.
		private enum BlockingSendOutcome
		{
			Delivered,
			TransientFail,
			PermanentDrop,  // 3xx / non-retryable 4xx / single-event 413 -- drop, re-sending can never succeed
			SplitPayload,
		}

		// One blocking POST via HttpClient. Drives the connect -> request -> response
		// state machine by hand (Poll + a short sleep), re-checking the shared budget at
		// every step so a stalled connect / handshake / response cannot overrun the quit
		// budget. Connects by hostname and lets HttpClient resolve: the IPv4-preference
		// plan of the async path is a per-flush latency optimization not worth
		// duplicating on the one-shot teardown drain, and the budget already caps a slow
		// resolve. A connect/dispatch/budget failure is TRANSIENT; only an actual permanent
		// HTTP status (via RetryPolicy.IsPermanentRejection) is a PermanentDrop.
		// <paramref name="budgetOverran"/> is OR-set when a 2xx confirmation was observed
		// only after the budget elapsed (the response landed during the final poll): the
		// batch is still acked as Delivered, but the public FlushBlocking result must report
		// false for the overrun. Never cleared here (OR across chunks).
		private BlockingSendOutcome PostBlocking(byte[] payload, int eventCount, Stopwatch elapsed, int budgetMs, ref bool budgetOverran)
		{
			if (!BlockingRequestTarget.TryParse(_endpointUrl, out var target))
			{
				GD.PushError($"[Framedash] Shutdown drain could not parse endpoint; dropping {eventCount} events.");
				return BlockingSendOutcome.TransientFail;
			}

			var client = new HttpClient();
			try
			{
				TlsOptions? tls = target.UseTls ? TlsOptions.Client() : null;
				if (client.ConnectToHost(target.Host, target.Port, tls) != Error.Ok) return BlockingSendOutcome.TransientFail;

				while (true)
				{
					HttpClient.Status status = client.GetStatus();
					if (status == HttpClient.Status.Connected) break;
					if (status != HttpClient.Status.Resolving && status != HttpClient.Status.Connecting)
						return BlockingSendOutcome.TransientFail;
					client.Poll();
					// Re-check IMMEDIATELY after the poll: if it advanced the state (connected
					// or terminal), act on it now instead of sleeping first -- a fixed post-poll
					// sleep both wastes budget and can carry an on-time transition past the
					// deadline. Sleep only while still resolving/connecting, capped to the
					// remaining budget so the loop can never sleep past the deadline.
					status = client.GetStatus();
					if (status != HttpClient.Status.Resolving && status != HttpClient.Status.Connecting)
						continue;
					long remainingMs = budgetMs - elapsed.ElapsedMilliseconds;
					if (remainingMs <= 0) return BlockingSendOutcome.TransientFail;
					OS.DelayMsec(RetryPolicy.NextPollDelayMs(remainingMs, BlockingPollMs));
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
					return BlockingSendOutcome.TransientFail;

				while (client.GetStatus() == HttpClient.Status.Requesting)
				{
					client.Poll();
					// Sleeping first could carry an on-time 2xx past the deadline (misreported as a late
					// confirmation), and concluding "timeout" without looking would classify a server-ACCEPTED
					// batch as transient -- the requeue would then duplicate every event (insert + charge) on
					// the next flush. Reading an already-completed response is bounded work (no further
					// polling), so this also covers the poll that completes exactly at the deadline: the
					// response is still read, a 2xx becoming a late-confirmed delivery (budgetOverran) and a
					// non-2xx following normal classification (ShouldReadCompletedResponseAtDeadline is the
					// pure spec of that rule).
					if (RetryPolicy.ShouldReadCompletedResponseAtDeadline(
							client.GetStatus() != HttpClient.Status.Requesting, client.HasResponse()))
						break;
					if (client.GetStatus() != HttpClient.Status.Requesting)
						return BlockingSendOutcome.TransientFail;
					long remainingMs = budgetMs - elapsed.ElapsedMilliseconds;
					if (remainingMs <= 0) return BlockingSendOutcome.TransientFail;
					// Sleep capped to the remaining budget so the loop can never overshoot it
					// (a fixed sleep would with timeoutMs < the interval or near the deadline).
					OS.DelayMsec(RetryPolicy.NextPollDelayMs(remainingMs, BlockingPollMs));
				}

				int code = client.GetResponseCode();
				if (code >= 200 && code < 300)
				{
					// A 2xx observed on the poll that crossed the deadline is still a real
					// confirmation: ACK it as Delivered (dequeue, never resend -- a resend
					// would only dup) but flag the overrun so the PUBLIC FlushBlocking result
					// honestly reports false: the caller asked for confirmation WITHIN the
					// budget, and this one arrived after it (mirrors the Unity SDK semantics).
					if (elapsed.ElapsedMilliseconds >= budgetMs) budgetOverran = true;
					if (VerboseLogging) GD.Print(TransportLog.FormatFlushSuccess(eventCount, code));
					return BlockingSendOutcome.Delivered;
				}
				// Multi-event 413: the server rejected the payload as too large, so ask the
				// caller to halve and resend (the pure ShouldSplitBatch mirror of the async
				// RetryAction.SplitBatch). Without this, a MaxPayloadBytes configured above
				// the real endpoint limit would make FlushBlocking requeue the same oversized
				// batch and deterministically fail forever, even though its halves deliver.
				if (_retryPolicy.ShouldSplitBatch(code, eventCount))
					return BlockingSendOutcome.SplitPayload;
				// canSplit == eventCount > 1: a single-event 413 cannot be split any further,
				// so it is permanent (drop) -- matching the async path, which returns Fail for
				// an unsplittable 413. (The splittable-413 case was returned above, so this
				// classifier only ever sees 413 with a single event here.)
				if (_retryPolicy.IsPermanentRejectionForChunk(code, canSplit: eventCount > 1))
				{
					GD.PushWarning($"[Framedash] Blocking send permanently rejected (HTTP {code}); dropping {eventCount} events.");
					return BlockingSendOutcome.PermanentDrop;
				}
				GD.PushWarning($"[Framedash] Blocking send failed (HTTP {code}); keeping {eventCount} events for retry.");
				return BlockingSendOutcome.TransientFail;
			}
			finally
			{
				client.Close();
			}
		}
	}
}
