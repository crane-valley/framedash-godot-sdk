#nullable enable

namespace Framedash
{
    /// <summary>
    /// Pure batch-sizing policy for the telemetry transport. No engine
    /// dependencies, so it is unit-testable under NextUnit (TransportLayer itself is
    /// engine-coupled via Godot's HttpRequest and excluded from the test assembly).
    ///
    /// The split decision keys off the SERVER per-request caps, not the per-flush
    /// batch threshold: a normal sub-cap drain is sent as a single request (then
    /// bounded only by the payload-byte limit), so a stall/burst drain is not
    /// fragmented into many tiny requests.
    /// </summary>
    public static class BatchPolicy
    {
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

        // Rough per-event byte estimate for the FIXED-SIZE parts only: numeric fields
        // (position, timestamp, perf scalars) plus protobuf tag/length framing for every
        // field. String field CONTENT is counted separately below at actual length x the
        // UTF-8 worst case -- legal field values (128-char event name / map id / player id
        // / build id, 64-char platform / engine version) can add ~2KB UTF-8 per event, far
        // beyond any flat constant. Deliberately generous so the estimate is not an
        // undercount that lets an oversized chunk through.
        private const int EventBaseByteEstimate = 256;
        private const int MapEntryOverheadBytes = 8;
        private const int MetricEntryByteEstimate = 16;
        // Protobuf serializes strings as UTF-8, but C# string.Length counts UTF-16 units.
        // A BMP char (e.g. CJK) is one UTF-16 unit but up to 3 UTF-8 bytes, and a
        // supplementary char is 2 UTF-16 units (a surrogate pair) at 4 UTF-8 bytes = 2
        // bytes/unit, so 3 bytes per UTF-16 unit is a safe upper bound for both. Without
        // this multiplier the estimate could undercount CJK-heavy content ~3x and let an
        // uninterruptible serialize+gzip overrun MaxBlockingChunkBytes. Kept mirrored with
        // the Unity/UE5 estimators.
        private const int Utf8WorstCaseBytesPerUtf16Unit = 3;

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
        /// String lengths are UTF-16 char counts scaled by
        /// <see cref="Utf8WorstCaseBytesPerUtf16Unit"/> (protobuf serializes UTF-8), which
        /// tracks serialize/gzip CPU cost closely enough for a chunking threshold without
        /// undercounting CJK-heavy content.
        /// </summary>
        public static long EstimateSerializedBytes(TelemetryEvent[]? events)
        {
            if (events == null) return 0;
            long total = 0;
            for (int i = 0; i < events.Length; i++)
            {
                total += EventBaseByteEstimate;
                // Per-event string FIELDS, counted at actual length like the map entries
                // (same x3 UTF-8 worst case). The base constant covers only fixed/numeric
                // framing; a batch of legal max-length CJK field values would otherwise be
                // underestimated by ~2KB UTF-8 per event and blow the serialize bound.
                // Field set mirrors TelemetryEvent's string fields (and the Unity/UE5
                // estimators): event name, session id, player id, map id, build id,
                // platform, engine version.
                var e = events[i];
                total += Utf8WorstCaseBytesPerUtf16Unit
                    * ((e.EventName != null ? e.EventName.Length : 0)
                        + (e.SessionId != null ? e.SessionId.Length : 0)
                        + (e.PlayerId != null ? e.PlayerId.Length : 0)
                        + (e.MapId != null ? e.MapId.Length : 0)
                        + (e.BuildId != null ? e.BuildId.Length : 0)
                        + (e.Platform != null ? e.Platform.Length : 0)
                        + (e.EngineVersion != null ? e.EngineVersion.Length : 0));
                var attributes = events[i].Attributes;
                var metrics = events[i].Metrics;
                if (attributes != null)
                {
                    for (int a = 0; a < attributes.Count; a++)
                    {
                        string key = attributes[a].Key;
                        string value = attributes[a].Value;
                        // x3: UTF-16 units -> worst-case UTF-8 bytes (see the const comment).
                        total += Utf8WorstCaseBytesPerUtf16Unit
                                * ((key != null ? key.Length : 0) + (value != null ? value.Length : 0))
                            + MapEntryOverheadBytes;
                    }
                }
                if (metrics != null)
                {
                    for (int m = 0; m < metrics.Count; m++)
                    {
                        string key = metrics[m].Key;
                        total += Utf8WorstCaseBytesPerUtf16Unit * (key != null ? key.Length : 0)
                            + MetricEntryByteEstimate;
                    }
                }
            }
            return total;
        }

        /// <summary>
        /// A single event is never split here: it cannot exceed the per-event server caps by
        /// enough to matter, and there is nothing smaller to send.
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
