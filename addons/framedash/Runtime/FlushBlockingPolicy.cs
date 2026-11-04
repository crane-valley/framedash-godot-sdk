#nullable enable

namespace Framedash
{
    /// <summary>
    /// Pure decision logic for the public synchronous flush (TelemetrySDK.FlushBlocking).
    /// No engine dependencies, so it is unit-testable under NextUnit (TelemetrySDK itself is
    /// Godot-coupled and excluded from the test assembly). Covers the timeout validation,
    /// budget clamp, and the no-loss re-enqueue decision so those rules are exercised
    /// without a running SceneTree.
    /// </summary>
    public static class FlushBlockingPolicy
    {
        /// <summary>
        /// Wall-clock cap for a public synchronous flush. Larger than the terminal
        /// shutdown drain budget (the game explicitly chose a moment it can afford to
        /// block), but still bounded so a wedged endpoint can never hang the game.
        /// </summary>
        public const int MaxBudgetMs = 30000;

        /// <summary>
        /// A non-positive timeout is a fast-fail: the caller asked for no budget, so there
        /// is no point acquiring the buffer or dialing the endpoint. Reported as failure.
        /// </summary>
        public static bool IsValidTimeout(int timeoutMs) => timeoutMs > 0;

        public static int ClampBudgetMs(int timeoutMs)
            => timeoutMs > MaxBudgetMs ? MaxBudgetMs : timeoutMs;

        /// <summary>
        /// Give the optional server-clock bootstrap at most half of a short caller
        /// budget and at most 500 ms overall. A one-millisecond flush skips HEAD so
        /// every supported positive timeout preserves delivery opportunity.
        /// </summary>
        public static int ServerTimeBootstrapBudgetMs(int budgetMs)
        {
            if (budgetMs <= 1) return 0;
            int halfBudgetMs = budgetMs / 2;
            return halfBudgetMs < 500 ? halfBudgetMs : 500;
        }

        /// <summary>
        /// Index of the in-flight envelope in the array built by
        /// <see cref="BatchPolicy.BuildShutdownEnvelopes"/>: it follows the buffered
        /// envelope, so 1 when a buffered envelope is present, else 0.
        /// </summary>
        public static int InFlightIndex(bool hasBuffered) => hasBuffered ? 1 : 0;

        /// <summary>
        /// The envelope index the blocking sender must treat as NON-splittable -- the in-flight
        /// envelope, whose events a parked async flush still owns and resends as the SAME
        /// original array. Blocking mode must send it in the identical shape or defer it, since
        /// ANY reshape (wire-cap, payload, or blocking-only chunk-bytes split) would diverge the
        /// batch hashes and double-insert. Returns -1 when there is no in-flight envelope. The
        /// buffered envelope has no async owner, so it is never marked (its delivered chunks are
        /// acked and its failed chunks never reached ingest, so its shape is irrelevant to dedup).
        /// </summary>
        public static int UnreshapedEnvelopeIndex(bool hasBuffered, bool hasInFlight)
            => hasInFlight ? InFlightIndex(hasBuffered) : -1;

        internal static bool ShouldDeferUnpreparedEnvelope(
            bool isUnreshaped,
            bool isPrepared,
            bool asyncCanResume)
            => isUnreshaped && asyncCanResume && !isPrepared;

        internal static bool ShouldCorrectEnvelopeTimestamps(
            bool isUnreshaped,
            bool isPrepared)
            => !isUnreshaped || !isPrepared;

        /// <summary>
        /// The buffered envelope's unconfirmed events (chunk-level) to re-enqueue after a
        /// non-destructive blocking flush, so nothing is lost. The buffered envelope is
        /// ALWAYS the first element (buffered-first), and <paramref name="undelivered"/> is
        /// PARALLEL to the envelope array with each slot holding exactly the events of that
        /// envelope the transport could not confirm (empty = fully delivered). Returning only
        /// the unconfirmed events -- not the whole buffered array -- is what prevents a
        /// delivered chunk from being resent: re-chunked differently on a later async flush it
        /// would hash to a different batch token and dodge the consumer's dedup, duplicating
        /// events that already reached ingest.
        ///
        /// The in-flight envelope is deliberately NOT re-enqueued here: the parked async flush
        /// still owns it (see <see cref="ShouldRetainInFlight"/>).
        /// </summary>
        public static TelemetryEvent[] BufferedUndelivered(bool hasBuffered, TelemetryEvent[][]? undelivered)
        {
            if (!hasBuffered || undelivered == null || undelivered.Length == 0)
                return System.Array.Empty<TelemetryEvent>();
            return undelivered[0] ?? System.Array.Empty<TelemetryEvent>();
        }

        /// <summary>
        /// Retain it when ANY of its events were not confirmed delivered (or there is no
        /// confirmation at all): otherwise, in the pre-quit case, the parked async flush can
        /// never complete after tree teardown and a later shutdown drain would see a null slot
        /// and lose the events -- violating the no-loss-on-false contract. The ORIGINAL array
        /// is restored (not a subset) so a shutdown drain resends it via the SAME blocking
        /// split -- identical batch hashes, deduped -- and so the owning async flush's
        /// ReferenceEquals guard still matches. Clear it (return false) only once every event
        /// was confirmed.
        /// </summary>
        public static bool ShouldRetainInFlight(bool hasBuffered, bool hasInFlight, TelemetryEvent[][]? undelivered)
        {
            if (!hasInFlight) return false;
            if (undelivered == null) return true;
            int index = InFlightIndex(hasBuffered);
            if (index >= undelivered.Length) return true;
            var events = undelivered[index];
            return events != null && events.Length > 0;
        }

        public static bool AllDelivered(TelemetryEvent[][]? undelivered)
        {
            if (undelivered == null) return false;
            for (int i = 0; i < undelivered.Length; i++)
                if (undelivered[i] != null && undelivered[i].Length > 0) return false;
            return true;
        }

        /// <summary>
        /// The final FlushBlocking result. True only when every envelope was fully confirmed
        /// AND nothing was permanently dropped AND no confirmation arrived after the budget.
        /// A permanent drop fails the result even though its events were dropped by policy
        /// (surfaced, not silent success); a budget overrun fails it even though the
        /// late-confirmed events were acked and not resent -- the false only reports that
        /// confirmation-within-budget did not happen (Unity-mirrored semantics).
        /// </summary>
        public static bool OverallResult(TelemetryEvent[][]? undelivered, bool anyPermanentDrop, bool budgetOverran)
            => AllDelivered(undelivered) && !anyPermanentDrop && !budgetOverran;

        /// <summary>
        /// The events to re-buffer after an async flush (or any resend) RESOLVES, given the
        /// events it could not confirm (empty/null = fully delivered). It is exactly the
        /// unconfirmed subset -- NEVER the whole original batch -- so a child that was split
        /// off and delivered during the send is not resent: re-chunked on a later flush it
        /// would hash to a different batch token and dodge the consumer's array-hash dedup,
        /// duplicating events that already reached ingest.
        /// </summary>
        public static TelemetryEvent[] ResolvedRequeue(TelemetryEvent[]? undelivered)
            => undelivered ?? System.Array.Empty<TelemetryEvent>();
    }
}
