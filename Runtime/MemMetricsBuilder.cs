#nullable enable

using System.Collections.Generic;

namespace Framedash
{
    /// <summary>
    /// Builds the mem.* metrics list attached to perf_heartbeat from sampled
    /// GPU-memory monitor values (Godot Performance.Monitor.RenderVideoMemUsed /
    /// RenderTextureMemUsed / RenderBufferMemUsed). Engine-independent (no Godot
    /// types) so the omit rule is unit-testable without an engine: a monitor value
    /// is only ever populated by Godot on the main thread when the RenderingServer
    /// backend is actually running, so a headless / unavailable reading surfaces as
    /// 0 (or a caught-exception 0 upstream in PerformanceCollector) -- absent =
    /// not collected, never a stuffed 0.
    /// </summary>
    public static class MemMetricsBuilder
    {
        public const string KeyVram = "mem.vram";
        public const string KeyTextures = "mem.textures";
        public const string KeyBuffers = "mem.buffers";

        /// <summary>
        /// Build the mem.* metrics list from raw monitor readings (bytes). Each key
        /// is included only if its value is finite and strictly positive -- a
        /// non-positive or non-finite reading omits that key entirely rather than
        /// emitting 0. Unlike the Tier-1 memory_used_bytes proto field, the metrics
        /// map has no ingest range ceiling beyond finiteness (see ClampMetrics), so
        /// the raw reading is passed through uncapped -- a large workstation GPU
        /// (e.g. 80/96 GiB VRAM) must not be silently truncated, and the other SDKs
        /// emit the full reading. Returns null if all three are absent, matching the
        /// "no metrics -> null" convention used elsewhere in this SDK.
        /// </summary>
        public static List<FloatPair>? Build(double vramBytes, double texturesBytes, double buffersBytes)
        {
            List<FloatPair>? metrics = null;
            AppendIfPositive(ref metrics, KeyVram, vramBytes);
            AppendIfPositive(ref metrics, KeyTextures, texturesBytes);
            AppendIfPositive(ref metrics, KeyBuffers, buffersBytes);
            return metrics;
        }

        private static void AppendIfPositive(ref List<FloatPair>? metrics, string key, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0.0) return;

            // Narrowing to float (the wire type) can itself overflow to +Infinity for an
            // extreme double reading; ingest rejects a non-finite metric value (dropping
            // the whole batch), so drop this key rather than pass through Infinity.
            float floatValue = (float)value;
            if (float.IsInfinity(floatValue)) return;

            metrics ??= new List<FloatPair>(3);
            metrics.Add(new FloatPair(key, floatValue));
        }

        /// <summary>
        /// Merge an already-built mem.* list (from <see cref="Build"/>) into
        /// <paramref name="existing"/> (typically a caller/event's already-built metrics
        /// list, e.g. user-supplied Track() metrics or the io.* heartbeat list).
        ///
        /// Takes a precomputed <paramref name="memMetrics"/> rather than raw monitor
        /// readings ON PURPOSE: the caller (TelemetrySDK) samples the monitors once per
        /// heartbeat interval and caches the resulting list by reference, then passes
        /// that SAME cached instance into this method for every position-qualified event
        /// tracked before the next heartbeat -- calling <see cref="Build"/> here instead
        /// would allocate a brand-new List&lt;FloatPair&gt; on every qualifying Track()
        /// call, which is a per-event heap allocation on the spatial telemetry hot path
        /// (the SDK's allocation-discipline hard rule forbids that; see CLAUDE.md).
        /// Handing out the cached reference is safe ONLY because it is treated as
        /// immutable once published: this method never mutates <paramref name="memMetrics"/>
        /// itself (only reads it), and the caller must REPLACE its cached field with a new
        /// list on each refresh rather than mutate the list already handed out --
        /// TelemetryEvent structs holding a reference to it can sit unflushed in the
        /// EventBuffer ring buffer (up to ~30s) alongside other events from the same or a
        /// later refresh, so an in-place mutation would retroactively alter an
        /// already-buffered event.
        ///
        /// A mem.* key is skipped if <paramref name="existing"/> already contains that
        /// exact key -- caller-supplied metrics are never clobbered. Entries are also
        /// capped so the merged list never exceeds FieldClamp.MaxMetrics (the same
        /// 50-key ingest limit the Track() path clamps user metrics to via
        /// ClampMetrics, which always runs before this merge) -- a caller event already
        /// at the cap gets nothing appended, unchanged from behavior before this
        /// feature existed. When only partial capacity remains, mem.* keys are added in
        /// <paramref name="memMetrics"/>'s existing order (vram, then textures, then
        /// buffers, per <see cref="Build"/>) so the result is deterministic.
        ///
        /// Returns <paramref name="existing"/> unchanged if <paramref name="memMetrics"/>
        /// is null or no capacity remains. Returns <paramref name="memMetrics"/> itself
        /// (the shared cached reference, not a copy) if <paramref name="existing"/> was
        /// null -- this is the per-event-allocation-free path.
        /// </summary>
        public static List<FloatPair>? AttachTo(List<FloatPair>? existing, List<FloatPair>? memMetrics)
        {
            if (memMetrics == null) return existing;
            if (existing == null) return memMetrics;

            int remainingCapacity = FieldClamp.MaxMetrics - existing.Count;
            if (remainingCapacity <= 0) return existing;

            foreach (var pair in memMetrics)
            {
                if (remainingCapacity <= 0) break;
                if (ContainsKey(existing, pair.Key)) continue;
                existing.Add(pair);
                remainingCapacity--;
            }
            return existing;
        }

        private static bool ContainsKey(List<FloatPair> metrics, string key)
        {
            for (int i = 0; i < metrics.Count; i++)
            {
                if (metrics[i].Key == key) return true;
            }
            return false;
        }
    }
}
