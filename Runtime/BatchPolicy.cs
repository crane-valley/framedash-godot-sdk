#nullable enable

namespace Framedash
{
    /// <summary>
    /// Pure batch-sizing policy for the telemetry transport. No engine
    /// dependencies, so it is unit-testable under NUnit (TransportLayer itself is
    /// engine-coupled via Godot's HttpRequest and excluded from the test assembly).
    ///
    /// The split decision keys off the SERVER per-request caps, not the per-flush
    /// batch threshold: a normal sub-cap drain is sent as a single request (then
    /// bounded only by the payload-byte limit), so a stall/burst drain is not
    /// fragmented into many tiny requests.
    /// </summary>
    public static class BatchPolicy
    {
        /// <summary>
        /// Server-side per-request event cap (mirrors
        /// packages/ingest-core/src/config.ts MAX_EVENTS_PER_BATCH). The consumer
        /// rejects a batch with more events than this wholesale.
        /// </summary>
        public const int MaxEventsPerBatch = 10000;

        /// <summary>
        /// Server-side per-request decoded-object cap (mirrors
        /// packages/ingest-core/src/config.ts MAX_DECODED_ENTRIES): events PLUS
        /// every attributes/metrics map entry across all events. The consumer
        /// rejects a batch whose total exceeds this wholesale, even when the event
        /// count, the per-event attribute/metric counts, and the gzip payload size
        /// are each within their own limits -- e.g. 10,000 events with 10 attributes
        /// each is 110,000 entries. The SDK must chunk on this too, otherwise such a
        /// batch is sent whole and dropped by the server.
        /// </summary>
        public const int MaxDecodedEntries = 100000;

        /// <summary>
        /// Count the decoded entries in a batch the way the consumer does: one per
        /// event plus one per attributes entry plus one per metrics entry.
        /// </summary>
        public static int CountDecodedEntries(TelemetryEvent[]? events)
        {
            if (events == null) return 0;
            // TelemetryEvent is a struct (value type), so every array element is a
            // fully-initialized value -- there are no null elements to guard, and
            // events[i].Attributes/Metrics cannot throw NullReferenceException (an
            // unset map is simply a null List, handled below). Start the total at one
            // entry per event, then add each event's map entries.
            int total = events.Length;
            for (int i = 0; i < events.Length; i++)
            {
                var attributes = events[i].Attributes;
                var metrics = events[i].Metrics;
                if (attributes != null) total += attributes.Count;
                if (metrics != null) total += metrics.Count;
            }
            return total;
        }

        /// <summary>
        /// Whether the batch must be chunked before sending because it would be
        /// rejected wholesale by a server per-request cap -- either the event-count
        /// wire cap or the decoded-entry cap (events + all map entries). A batch of
        /// one (or zero) is never split here: a single oversized event is bounded by
        /// the payload-byte path or dropped on a 413, and the server enforces the
        /// per-event attribute/metric caps that splitting cannot fix.
        /// </summary>
        public static bool ExceedsWireCaps(TelemetryEvent[]? events)
        {
            if (events == null || events.Length <= 1) return false;
            return events.Length > MaxEventsPerBatch
                || CountDecodedEntries(events) > MaxDecodedEntries;
        }
    }
}
