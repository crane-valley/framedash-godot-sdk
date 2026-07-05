#nullable enable

using System;
using System.Collections.Generic;

namespace Framedash
{
    /// <summary>
    /// Engine-independent client-side clamps for per-event fields. The ingest
    /// validator (packages/ingest-core/src/validation.ts) rejects the WHOLE batch
    /// if any single event field violates a server limit (after the server already
    /// returned 202), so one over-length attribute or one NaN coordinate would
    /// silently drop every event in that flush. These helpers clamp each field to
    /// the ingest caps (packages/ingest-core/src/config.ts) before the event is
    /// buffered. Kept identical to the Unity SDK's FieldClamp so both C# engines
    /// behave the same. No Godot references -- pure logic, NUnit-tested.
    /// </summary>
    public static class FieldClamp
    {
        // Must stay in sync with packages/ingest-core/src/config.ts.
        public const int MaxEventNameLength = 128;
        public const int MaxMapIdLength = 128;
        public const int MaxBuildIdLength = 128;
        public const int MaxPlatformLength = 64;
        public const int MaxEngineVersionLength = 64;
        public const int MaxAttributes = 50;
        public const int MaxMetrics = 50;
        public const int MaxAttributeKeyLength = 64;
        public const int MaxAttributeValueLength = 512;
        public const float PositionAbsMax = 1e9f;
        public const float FpsMax = 1000f;
        public const float TimingMsMax = 10000f;
        // Matches packages/ingest-core/src/config.ts MEMORY_USED_BYTES_MAX (64 GiB).
        public const long MaxMemoryUsedBytes = 64L * 1024L * 1024L * 1024L;

        /// <summary>
        /// Truncate an event name to the ingest cap. Over-cap names are rejected by
        /// ingest validation (dropping the whole batch), so clamp client-side.
        /// </summary>
        public static string TruncateEventName(string eventName)
        {
            return Truncate(eventName, MaxEventNameLength);
        }

        /// <summary>
        /// Truncate a string field to <paramref name="maxLength"/> UTF-16 code units
        /// (matching the server's JS string-length semantics). If the boundary would
        /// split a surrogate pair, drop the dangling high surrogate so the result is
        /// always valid UTF-16 rather than a lone surrogate (which would serialize to
        /// a replacement char on the wire). Always returns non-null (a null value
        /// maps to ""): a Godot addon's .cs files compile directly into the host
        /// project, so every Godot-coupled call site (e.g. TelemetrySDK's cached
        /// non-nullable fields) must get a non-nullable result without each one
        /// having to null-forgive or null-coalesce this helper's return value.
        /// </summary>
        public static string Truncate(string? value, int maxLength)
        {
            if (maxLength <= 0) return "";
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength) return value ?? "";
            int len = maxLength;
            if (char.IsHighSurrogate(value[len - 1])) len--;
            return value.Substring(0, len);
        }

        /// <summary>
        /// Map a position coordinate to a safe finite value. NaN/Infinity or
        /// |coordinate| > 1e9 are rejected by ingest validation (dropping the whole
        /// batch), so one bad physics frame does not lose unrelated telemetry.
        /// </summary>
        public static float SanitizeCoord(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0f;
            if (v > PositionAbsMax) return PositionAbsMax;
            if (v < -PositionAbsMax) return -PositionAbsMax;
            return v;
        }

        /// <summary>
        /// Clamp a timing value (frame/gpu/render/game-thread ms) to the ingest
        /// range [0, 10000]. NaN or negative maps to 0 (the proto contract treats
        /// 0 as "not collected").
        /// </summary>
        public static float ClampTimingMs(float v)
        {
            if (float.IsNaN(v) || v < 0f) return 0f;
            return Math.Min(v, TimingMsMax);
        }

        /// <summary>
        /// Derive FPS from a frame time in milliseconds, clamped to the ingest FPS
        /// ceiling (1000). A sub-1ms frame (uncapped/headless) would otherwise report
        /// fps > 1000 and the batch is rejected. Returns 0 for a non-positive frame.
        /// </summary>
        public static float FpsFromFrameTimeMs(float frameTimeMs)
        {
            return frameTimeMs > 0f ? Math.Min(FpsMax, 1000f / frameTimeMs) : 0f;
        }

        /// <summary>
        /// Clamp a memory-used value to the ingest range [0, 64 GiB]. Ingest rejects
        /// a value outside this range (dropping the whole batch), so floor a negative
        /// or garbage reading at 0 and cap an oversized one at the ceiling.
        /// </summary>
        public static long ClampMemory(long v)
        {
            if (v < 0L) return 0L;
            return Math.Min(v, MaxMemoryUsedBytes);
        }

        /// <summary>
        /// Convert an attributes dictionary to the serializable list form, enforcing
        /// the ingest caps (count, key/value length). A null dictionary maps to a
        /// null list -- callers rely on this "no attributes -> null" semantics. Entries
        /// with a null/empty key are skipped; the count is capped at 50.
        /// </summary>
        public static List<StringPair>? ClampAttributes(Dictionary<string, string>? attrs)
        {
            if (attrs == null) return null;
            var list = new List<StringPair>(Math.Min(attrs.Count, MaxAttributes));
            foreach (var kvp in attrs)
            {
                if (list.Count >= MaxAttributes) break;
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                list.Add(new StringPair(
                    Truncate(kvp.Key, MaxAttributeKeyLength),
                    Truncate(kvp.Value ?? "", MaxAttributeValueLength)));
            }
            return list;
        }

        /// <summary>
        /// Convert a metrics dictionary to the serializable list form, enforcing the
        /// ingest caps (count, key length) and dropping non-finite values. A null
        /// dictionary maps to a null list -- callers rely on this "no metrics -> null"
        /// semantics. Entries with a null/empty key or a NaN/Infinity value are
        /// skipped; the count is capped at 50.
        /// </summary>
        public static List<FloatPair>? ClampMetrics(Dictionary<string, float>? metrics)
        {
            if (metrics == null) return null;
            var list = new List<FloatPair>(Math.Min(metrics.Count, MaxMetrics));
            foreach (var kvp in metrics)
            {
                if (list.Count >= MaxMetrics) break;
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                // Non-finite metric values are rejected by ingest -> drop them.
                if (float.IsNaN(kvp.Value) || float.IsInfinity(kvp.Value)) continue;
                list.Add(new FloatPair(Truncate(kvp.Key, MaxAttributeKeyLength), kvp.Value));
            }
            return list;
        }
    }
}
