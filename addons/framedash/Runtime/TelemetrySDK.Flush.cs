#nullable disable

using System;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace Framedash
{
    public partial class TelemetrySDK : Node
    {
        public void Flush()
        {
            // Flush touches the scene tree / HttpRequest, which are main-thread only. If
            // called from a background thread (Track-from-thread is supported), marshal the
            // call to the main thread instead of risking an off-main engine call.
            if (OS.GetThreadCallerId() != OS.GetMainThreadId())
            {
                Callable.From(Flush).CallDeferred();
                return;
            }
            try
            {
                if (!_flushGate.TryBegin()) return;
                if (!_initialized || _buffer.Count == 0)
                {
                    // Nothing to flush -- clear any pending request so _Process does not
                    // re-invoke Flush() every frame while the buffer stays empty.
                    _flushRequested = false;
                    _flushGate.Release();
                    return;
                }
                // Reset _flushRequested AFTER the guard so a background-thread request
                // arriving between the two checks is not silently dropped.
                _flushRequested = false;
                Interlocked.Exchange(ref _estimatedPayloadBytes, 0);
                // Godot uses async/await rather than coroutines: launch the send
                // fire-and-forget. FlushAsync awaits the transport in a try/finally
                // that always releases the gate so a failed send cannot wedge the
                // single-flight guard. The bounded transport wall-time keeps that
                // window small so later flushes are not starved. The discard documents
                // the intentional non-await. Retain the batch so a shutdown that races
                // this unconfirmed send can resend it via the blocking drain.
                var batch = _buffer.DequeueAll();
                _inFlightBatch = batch;
                _ = FlushAsync(batch, _flushGate.Generation);
            }
            catch (Exception e)
            {
                _flushGate.Release();
                GD.PushError($"[Framedash] Flush() failed: {e.Message}");
            }
        }

        // Awaits the transport send and always releases the single-flight guard.
        // Async void would let an exception escape to the synchronization context;
        // returning Task with a try/catch keeps the SDK fail-safe even though the
        // caller discards the task.
        private async Task FlushAsync(TelemetryEvent[] events, int generation)
        {
            // The events this send could NOT confirm delivered. On an exception the whole
            // batch is treated as undelivered (nothing was confirmed). A split send reports
            // ONLY its failed children here, so a delivered child is never re-buffered.
            TelemetryEvent[] undelivered = events;
            try
            {
                undelivered = await _transport.SendBatch(events);
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] Flush send failed: {e.Message}");
            }
            finally
            {
                if (ReferenceEquals(_inFlightBatch, events))
                {
                    // The async flush has RESOLVED, so this batch is no longer in flight:
                    // always clear the whole-array retention. A racing shutdown drain or a
                    // FlushBlocking seize must never resend a whole array whose delivered
                    // children would ride along (they would re-chunk differently and dodge
                    // dedup). Then re-buffer ONLY the unconfirmed subset so a later flush
                    // retries just those -- instead of leaving them in a field the next
                    // Flush() would overwrite (permanent loss), and without resending any
                    // delivered child. Baseline dropped failed async batches outright;
                    // re-buffering the failed subset is the at-least-once improvement (a
                    // resent event can only duplicate if its original POST actually reached
                    // ingest -- accepted trade-off). No _flushRequested nudge: let the normal
                    // interval retry so a wedged endpoint cannot cause a per-frame reflush storm.
                    _inFlightBatch = null;
                    TelemetryEvent[] requeue = FlushBlockingPolicy.ResolvedRequeue(undelivered);
                    if (requeue.Length > 0) RequeueEvents(requeue);
                }
                // The gate release is UNCONDITIONAL (success or failure) so a failed send can
                // never wedge the single-flight guard; only the retention above is
                // outcome-gated. Release only if no re-init happened meanwhile; otherwise the
                // new generation owns the gate and clearing it here could let two sends run.
                _flushGate.ReleaseIfGeneration(generation);
            }
        }

        /// <summary>
        /// Unlike the best-effort shutdown drain this is NON-DESTRUCTIVE: events not confirmed
        /// within the budget stay buffered for the periodic async flush to retry, so nothing is
        /// lost. The no-loss guarantee covers TIMEOUT and TRANSIENT failures (5xx / 429 /
        /// network); a PERMANENT server rejection (3xx / non-retryable 4xx such as 401/400) is
        /// dropped by transport policy -- re-sending it could never succeed, so requeuing would
        /// poison the buffer forever -- and is surfaced by returning false. Returns true only
        /// when everything was confirmed delivered (or there was nothing to send); returns
        /// false on timeout, a transient or permanent transport failure, a disabled
        /// (endpoint-security fail-closed) or uninitialized SDK, `timeoutMs` &lt;= 0, or when
        /// called off the main thread (it does NOT marshal-and-block). A confirmation observed
        /// only after the budget elapsed is still acknowledged (the batch stays delivered,
        /// never resent); the false then reports only the budget overrun. `timeoutMs` is
        /// clamped to at most 30000. Never throws and never blocks meaningfully past the
        /// budget.
        /// </summary>
        public bool FlushBlocking(int timeoutMs)
        {
            // Off the main thread, blocking here would either sit behind the marshaling path
            // (which resumes only on the main loop we would be blocking) or touch scene-tree
            // / HttpClient state off-thread. Refuse rather than marshal-and-block.
            if (OS.GetThreadCallerId() != OS.GetMainThreadId())
            {
                GD.PushWarning("[Framedash] FlushBlocking must be called on the main thread; returning false.");
                return false;
            }
            try
            {
                if (!FlushBlockingPolicy.IsValidTimeout(timeoutMs)) return false;
                int budgetMs = FlushBlockingPolicy.ClampBudgetMs(timeoutMs);

                // Fail closed: an uninitialized SDK, or one whose endpoint failed the
                // transport-security check, delivers nothing -- report failure rather than
                // silently "succeeding" with no send.
                if (!_initialized || _transport == null || _transport.IsDisabled) return false;

                // Seize the batch a periodic async flush already dequeued but has not
                // confirmed (it is parked -- its awaited continuation cannot run while this
                // method blocks the main thread), exactly as the shutdown drain does. We do
                // NOT touch the FlushGate: that async flush still owns the transport's single
                // HttpRequest node, so freeing the gate here would let the next periodic
                // flush issue a second concurrent request on the same node. Leaving the gate
                // held means the async flush releases it (same generation, no stale
                // completion) when its request truly finishes, and the SDK resumes normal
                // periodic flushing afterward. We send the seized batch as its own envelope
                // below (the consumer dedups an identical resent array) purely to CONFIRM it
                // within the budget; we never re-enqueue it into the ring buffer, because the
                // async flush retains ownership and a re-enqueued copy would hash differently
                // and duplicate. The field is cleared here only provisionally: if the resend
                // is not confirmed we RESTORE the reference below, so a later shutdown drain
                // (the documented pre-quit case, where the parked async flush can never
                // complete after tree teardown) can still resend it instead of losing it.
                var inFlight = _inFlightBatch;
                _inFlightBatch = null;

                // Reset the payload estimate BEFORE dequeuing (the ordering Flush() uses): a
                // background Track() landing between a dequeue and a later reset would have
                // its byte increment erased, so in payload-triggered configs post-snapshot
                // events could wait on the 30s interval instead of the configured threshold.
                // With reset-first, a Track before the dequeue is captured by the snapshot
                // (its erased increment matches its flushed event) and a Track after it keeps
                // its increment for the next trigger check.
                Interlocked.Exchange(ref _estimatedPayloadBytes, 0);

                TelemetryEvent[] buffered = _buffer != null && _buffer.Count > 0
                    ? _buffer.DequeueAll()
                    : System.Array.Empty<TelemetryEvent>();

                // Buffered events (guaranteed-undelivered) go FIRST; the in-flight batch is a
                // separate envelope so the consumer's full-array hash dedups an already-
                // delivered resend (see BatchPolicy.BuildShutdownEnvelopes).
                TelemetryEvent[][] envelopes = BatchPolicy.BuildShutdownEnvelopes(buffered, inFlight);
                if (envelopes.Length == 0) return true;

                bool hasBuffered = buffered.Length > 0;
                bool hasInFlight = inFlight != null && inFlight.Length > 0;

                // Chunk-level result: the exact events of each envelope NOT confirmed
                // delivered (a split envelope reports only its failed chunks), so we
                // re-enqueue precisely the unconfirmed events and never resend a delivered
                // chunk -- which, re-chunked differently on a later async flush, would dodge
                // the consumer's array-hash dedup and duplicate. The in-flight envelope is
                // marked NON-splittable (UnreshapedEnvelopeIndex): it is sent in its identical
                // original shape or deferred (never reshaped), staying byte-identical to the
                // parked async flush that still owns it -- otherwise both succeeding would
                // double-insert every in-flight event.
                TelemetryEvent[][] undelivered = _transport.SendEnvelopesBlockingUndelivered(
                    envelopes, budgetMs, FlushBlockingPolicy.UnreshapedEnvelopeIndex(hasBuffered, hasInFlight),
                    out bool anyPermanentDrop, out bool budgetOverran);

                // No loss: re-enqueue only the buffered envelope's unconfirmed events (not the
                // whole array). The in-flight batch is handled by restoring its retention below.
                TelemetryEvent[] bufferedRetry = FlushBlockingPolicy.BufferedUndelivered(hasBuffered, undelivered);
                if (bufferedRetry.Length > 0)
                {
                    RequeueEvents(bufferedRetry);
                    // Nudge the periodic flush to retry promptly rather than waiting the full interval.
                    _flushRequested = true;
                }

                // No loss: restore the in-flight retention (the ORIGINAL array) when any of its
                // events were not confirmed. Safe to assign: the main thread was blocked
                // throughout the send, so no async continuation or new flush could have set
                // _inFlightBatch. The parked async flush that owns this batch still ref-matches
                // this same array when it resolves, then clears the slot and re-buffers only
                // its own unconfirmed subset via its finally -- so a delivered child is never
                // resent and the next Flush() can never silently overwrite an unconfirmed batch.
                if (FlushBlockingPolicy.ShouldRetainInFlight(hasBuffered, hasInFlight, undelivered))
                {
                    _inFlightBatch = inFlight;
                }

                // A permanent rejection also fails the result even though its events were
                // dropped (not requeued/retained), and so does a budget overrun even though
                // its late-confirmed events were acked (not resent): the caller sees false,
                // not a silent success, whenever confirmation-within-budget did not happen.
                return FlushBlockingPolicy.OverallResult(undelivered, anyPermanentDrop, budgetOverran);
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] FlushBlocking() failed: {e.Message}");
                return false;
            }
        }

        // Put un-confirmed events back into the ring buffer so a later flush retries them (no
        // loss). Order relative to events a background Track() enqueued meanwhile is not
        // preserved, which is harmless: the consumer orders by each event's timestamp_us, and
        // the ring buffer's drop-oldest-when-full policy still governs (game perf > telemetry
        // completeness). A resent copy can duplicate only if the original POST actually reached
        // ingest despite reporting failure -- the accepted at-least-once trade-off. The caller
        // decides whether to also nudge _flushRequested (FlushBlocking does; a failed async
        // flush does not, so a wedged endpoint cannot trigger a per-frame reflush storm).
        private void RequeueEvents(TelemetryEvent[] events)
        {
            if (_buffer == null || events == null) return;
            for (int i = 0; i < events.Length; i++)
            {
                _buffer.Enqueue(events[i]);
            }
        }

        // Deliver the buffered events SYNCHRONOUSLY on a terminal-quit path. The async
        // Flush() cannot complete once the app is quitting -- the main loop stops before
        // the awaited SendBatch continuation runs, and the owner node leaves the tree --
        // so a fire-and-forget send would only dequeue the buffer into a doomed task and
        // lose the final events (typically the last perf_heartbeat). The transport's
        // blocking HttpClient path needs no scene-tree node and is bounded so quit is
        // never hung. Main-thread only: every caller is an engine teardown callback or the
        // already-marshaled Shutdown.
        private void DrainBlocking()
        {
            // Called from Shutdown AFTER _initialized was flipped false (so no new events
            // are accepted); do not re-check _initialized here or the drain would no-op.
            if (_transport == null) return;

            // Recover a batch an async flush already dequeued but has not confirmed: its
            // awaited send cannot resume during synchronous teardown, so those events
            // (already gone from the buffer) would otherwise be lost.
            var inFlight = _inFlightBatch;
            _inFlightBatch = null;

            // Reset the payload estimate BEFORE dequeuing (same ordering as Flush() and
            // FlushBlocking). Shutdown already stopped accepting events (_initialized is
            // false), but a Track() that passed its _initialized check just before the flip
            // can still land between a dequeue and a later reset -- reset-first keeps its
            // increment consistent with whether its event made the final snapshot.
            Interlocked.Exchange(ref _estimatedPayloadBytes, 0);

            TelemetryEvent[] buffered = _buffer != null && _buffer.Count > 0
                ? _buffer.DequeueAll()
                : System.Array.Empty<TelemetryEvent>();

            // Send the buffered events and the in-flight batch as SEPARATE envelopes, never
            // concatenated: the consumer dedups by hashing the full ordered event array, so
            // re-sending the in-flight batch with its ORIGINAL array is dropped if it
            // already reached ingest, whereas merging it with the buffered events would
            // hash differently and duplicate every in-flight event (see
            // BatchPolicy.BuildShutdownEnvelopes). Buffered goes first (guaranteed-
            // undelivered final events). Both share one drain budget.
            TelemetryEvent[][] envelopes = BatchPolicy.BuildShutdownEnvelopes(buffered, inFlight);
            if (envelopes.Length == 0) return;

            // The in-flight envelope is NON-splittable here too: a parked async flush may have
            // already delivered some of its split children before quit stopped the main loop,
            // and reshaping the whole array (blocking-only chunk-bytes split) would resend
            // those delivered children under different batch hashes and double-insert. So it is
            // sent in its identical original shape or deferred (a splittable in-flight batch is
            // dropped -- best-effort, no offline queue -- rather than risk a duplicate). The
            // buffered envelope stays splittable (it has no async owner). Unlike FlushBlocking
            // there is no live async flush to fall back on, so this trades a rare partial-batch
            // loss for never double-charging.
            bool hasBuffered = buffered.Length > 0;
            bool hasInFlight = inFlight != null && inFlight.Length > 0;
            _transport.SendEnvelopesBlocking(envelopes, ShutdownDrainBudgetMs,
                FlushBlockingPolicy.UnreshapedEnvelopeIndex(hasBuffered, hasInFlight));
        }

        public void Shutdown()
        {
            // Shutdown mutates lifecycle state (_initialized/_shutdownCalled) and drives the
            // final Flush(), all of which touch the scene tree / must stay ordered on the main
            // thread. Marshal the WHOLE method to the main thread (like Flush) when called off
            // it: otherwise Flush() would defer itself and return, and _initialized=false would
            // then be set on the background thread BEFORE the deferred flush runs, so the
            // deferred final flush no-ops and silently drops the buffered events. Marshaling also
            // keeps _shutdownCalled a genuinely main-thread-only field.
            if (OS.GetThreadCallerId() != OS.GetMainThreadId())
            {
                Callable.From(Shutdown).CallDeferred();
                return;
            }
            try
            {
                if (!_initialized) return;
                // Stop accepting new events BEFORE taking the final snapshot. _initialized
                // is volatile and Track() bails on it, so a producer (possibly on a
                // background thread) cannot enqueue an event after this point that the
                // drain's DequeueAll would then miss and silently drop. (A Track already
                // past its _initialized check when the flag flips is an inherent last-
                // instant lock-free race and is unchanged by this ordering.) Latch the
                // shutdown too so a deferred tree entry cannot auto-init and revive the
                // instance mid-drain.
                _initialized = false;
                _shutdownCalled = true;
                // Final flush must be SYNCHRONOUS: _ExitTree tears the tree down in the
                // same call, so an async Flush() would resume after the node (and its
                // HttpRequest child) left the tree and drop the buffered events (the final
                // perf_heartbeat). Drain via the transport's blocking HttpClient path
                // instead, bounded so quit is never hung. Godot still has no offline queue
                // (that is UE5-only), so a send that fails within the budget is lost --
                // best-effort by design.
                DrainBlocking();
                GD.Print("[Framedash] SDK shut down.");
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] Shutdown() failed: {e.Message}");
            }
        }

        public override void _ExitTree()
        {
            if (s_instance == this)
            {
                Shutdown();
                s_instance = null;
            }
        }

        // Lifecycle notifications mirror Unity's OnApplicationPause / OnApplicationQuit
        // intent. Each handler is wrapped so no exception escapes the engine callback.
        public override void _Notification(int what)
        {
            try
            {
                switch (what)
                {
                    // App moved to background (mobile) — flush so a backgrounded/killed
                    // app does not lose buffered events. Unity: OnApplicationPause(true).
                    case (int)NotificationApplicationPaused:
                        Flush();
                        break;

                    // The window/OS requested a close. This is NOT necessarily terminal:
                    // a game may set SceneTree.AutoAcceptQuit=false to show a confirm
                    // dialog or reject the close, so draining here would block the main
                    // thread and permanently drop events even though the app keeps running.
                    // The terminal drain runs from _ExitTree/Predelete (Shutdown) once the
                    // quit is actually accepted, so nothing is needed here. Unity:
                    // OnApplicationQuit precursor.
                    case (int)NotificationWMCloseRequest:
                        break;

                    case (int)NotificationPredelete:
                        Shutdown();
                        break;
                }
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] _Notification({what}) failed: {e.Message}");
            }
        }
    }
}
