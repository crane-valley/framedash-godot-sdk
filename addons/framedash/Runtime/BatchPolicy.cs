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

        // Rough per-event fixed-field byte estimate (event name, ids, position,
        // platform, engine version, timestamp, protobuf framing) for the blocking-drain
        // pre-serialization size split. Deliberately generous so the estimate is not an
        // undercount that lets an oversized chunk through.
        private const int EventBaseByteEstimate = 256;
        // Protobuf key/length framing added per map entry on top of the key+value chars.
        private const int MapEntryOverheadBytes = 8;
        // Per-metric entry beyond its key chars: the float value plus framing.
        private const int MetricEntryByteEstimate = 16;

        /// <summary>
        /// Max estimated UNCOMPRESSED bytes a single blocking-drain serialize+gzip may
        /// process before the batch is split. serialize+gzip is uninterruptible, so this
        /// bounds one CPU unit: ~1 MiB serializes and gzips in a few milliseconds even on
        /// weak hardware, keeping the synchronous shutdown drain within its time budget.
        /// </summary>
        public const long MaxBlockingChunkBytes = 1 << 20;

        /// <summary>
        /// Conservative estimate of a batch's uncompressed serialized size in bytes, used
        /// ONLY to bound the work of one serialize+gzip on the synchronous shutdown drain.
        /// An attribute-heavy legal batch (up to <see cref="MaxDecodedEntries"/> map
        /// entries whose values can each be hundreds of chars) can otherwise be tens of
        /// MB, and a single uninterruptible serialize could overrun the drain budget.
        /// String lengths are UTF-16 char counts, which track serialize/gzip CPU cost
        /// closely enough for a chunking threshold.
        /// </summary>
        public static long EstimateSerializedBytes(TelemetryEvent[]? events)
        {
            if (events == null) return 0;
            long total = 0;
            for (int i = 0; i < events.Length; i++)
            {
                total += EventBaseByteEstimate;
                var attributes = events[i].Attributes;
                var metrics = events[i].Metrics;
                if (attributes != null)
                {
                    for (int a = 0; a < attributes.Count; a++)
                    {
                        string key = attributes[a].Key;
                        string value = attributes[a].Value;
                        total += (key != null ? key.Length : 0)
                            + (value != null ? value.Length : 0)
                            + MapEntryOverheadBytes;
                    }
                }
                if (metrics != null)
                {
                    for (int m = 0; m < metrics.Count; m++)
                    {
                        string key = metrics[m].Key;
                        total += (key != null ? key.Length : 0) + MetricEntryByteEstimate;
                    }
                }
            }
            return total;
        }

        /// <summary>
        /// Whether a batch must be split BEFORE serialization on the blocking shutdown
        /// drain to keep each serialize+gzip bounded (see <see cref="MaxBlockingChunkBytes"/>).
        /// A single event is never split here: it cannot exceed the per-event server caps
        /// by enough to matter, and there is nothing smaller to send.
        /// </summary>
        public static bool ExceedsBlockingChunkBytes(TelemetryEvent[]? events)
        {
            if (events == null || events.Length <= 1) return false;
            return EstimateSerializedBytes(events) > MaxBlockingChunkBytes;
        }

        /// <summary>
        /// Ordered list of INDEPENDENT envelopes to POST on the synchronous shutdown
        /// drain. The freshly-buffered events and a retained-but-unconfirmed in-flight
        /// batch are kept as SEPARATE envelopes, never concatenated: the consumer
        /// deduplicates by hashing the full ordered event array
        /// (apps/consumer/src/message-helpers.ts hashEventBatch), so re-sending the
        /// in-flight batch with its ORIGINAL array keeps the same dedup token and is
        /// dropped if it already reached ingest, whereas an "in-flight + buffered"
        /// concatenation would hash differently and duplicate every in-flight event (both
        /// charged and inserted). <paramref name="buffered"/> is ordered FIRST because it
        /// is guaranteed-undelivered (the final perf_heartbeat, the primary loss this
        /// drain fixes), so a wedged endpoint under the shared deadline cannot starve it
        /// behind a possibly-redundant in-flight resend. Empty/null batches are omitted.
        /// </summary>
        public static TelemetryEvent[][] BuildShutdownEnvelopes(
            TelemetryEvent[]? buffered, TelemetryEvent[]? inFlight)
        {
            bool haveBuffered = buffered != null && buffered.Length > 0;
            bool haveInFlight = inFlight != null && inFlight.Length > 0;
            if (haveBuffered && haveInFlight) return new[] { buffered!, inFlight! };
            if (haveBuffered) return new[] { buffered! };
            if (haveInFlight) return new[] { inFlight! };
            return System.Array.Empty<TelemetryEvent[]>();
        }
    }
}
