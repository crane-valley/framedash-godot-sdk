#nullable enable

using System;
using System.Globalization;

namespace Framedash
{
    internal enum AutoloadCollisionDecision
    {
        AcceptIncoming,
        PreserveExistingFallback,
        RejectIncoming,
    }

    /// <summary>
    /// Pure decisions shared by Godot-coupled lifecycle and sampling code.
    /// Keeping them outside Node/Performance APIs gives the public addon deterministic
    /// coverage without maintaining a second fake Godot runtime.
    /// </summary>
    internal static class GodotRuntimePolicy
    {
        internal const long MinTimestampUs = 946_684_800_000_000L;
        internal const long MaxTimestampUs = 4_102_444_800_000_000L;
        internal const long MaxPastAgeUs = 30L * 24L * 60L * 60L * 1_000_000L;
        internal const long MaxFutureSkewUs = 48L * 60L * 60L * 1_000_000L;
        internal const long TimestampSafetyMarginUs = 5L * 60L * 1_000_000L;
        internal const long MaxServerTimeObservationAgeUs =
            24L * 60L * 60L * 1_000_000L;

        internal static AutoloadCollisionDecision DecideAutoloadCollision(
            bool hasExistingInstance,
            bool existingIsFallback,
            bool incomingIsRegisteredAutoload)
        {
            if (!hasExistingInstance) return AutoloadCollisionDecision.AcceptIncoming;
            if (existingIsFallback && incomingIsRegisteredAutoload)
            {
                return AutoloadCollisionDecision.PreserveExistingFallback;
            }
            return AutoloadCollisionDecision.RejectIncoming;
        }

        internal static long SelectMemoryUsedBytes(long godotStaticBytes, long managedHeapBytes)
        {
            long selected = godotStaticBytes > 0L ? godotStaticBytes : managedHeapBytes;
            return FieldClamp.ClampMemory(selected);
        }

        internal static long ClampTimestampUs(long timestampUs, long nowUs)
        {
            long boundedNowUs = nowUs;
            if (boundedNowUs < MinTimestampUs) boundedNowUs = MinTimestampUs;
            if (boundedNowUs > MaxTimestampUs) boundedNowUs = MaxTimestampUs;

            long minimumAcceptedUs =
                boundedNowUs - MaxPastAgeUs + TimestampSafetyMarginUs;
            if (minimumAcceptedUs < MinTimestampUs) minimumAcceptedUs = MinTimestampUs;
            long maximumAcceptedUs =
                boundedNowUs + MaxFutureSkewUs - TimestampSafetyMarginUs;
            if (maximumAcceptedUs > MaxTimestampUs) maximumAcceptedUs = MaxTimestampUs;

            if (timestampUs < minimumAcceptedUs) return boundedNowUs;
            if (timestampUs > maximumAcceptedUs) return boundedNowUs;
            return timestampUs;
        }

        internal static bool TryReadServerTimestampUs(
            string[] responseHeaders,
            out long timestampUs)
        {
            timestampUs = 0L;
            if (responseHeaders == null) return false;
            for (int i = 0; i < responseHeaders.Length; i++)
            {
                string header = responseHeaders[i];
                if (header == null) continue;
                int separator = header.IndexOf(':');
                if (separator < 0 ||
                    !header.Substring(0, separator).Trim().Equals(
                        "Date",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                if (!DateTimeOffset.TryParse(
                        header.Substring(separator + 1).Trim(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset serverTime))
                {
                    return false;
                }
                timestampUs = serverTime.ToUnixTimeMilliseconds() * 1_000L;
                return timestampUs >= MinTimestampUs &&
                    timestampUs <= MaxTimestampUs;
            }
            return false;
        }

        internal static long EstimateServerNowUs(
            long observedServerTimeUs,
            long observedMonotonicTicks,
            long currentMonotonicTicks,
            long monotonicFrequency,
            long localFallbackUs = 0L)
        {
            if (observedServerTimeUs <= 0L ||
                monotonicFrequency <= 0L ||
                currentMonotonicTicks < observedMonotonicTicks)
            {
                return localFallbackUs;
            }

            double elapsedUs =
                (currentMonotonicTicks - observedMonotonicTicks) *
                (1_000_000d / monotonicFrequency);
            if (elapsedUs >= long.MaxValue - observedServerTimeUs)
                return long.MaxValue;
            return observedServerTimeUs + (long)elapsedUs;
        }

        internal static bool IsServerTimeObservationFresh(
            long observedLocalTimeUs,
            long currentLocalTimeUs,
            long observedMonotonicTicks,
            long currentMonotonicTicks,
            long monotonicFrequency)
        {
            if (observedLocalTimeUs <= 0L ||
                currentLocalTimeUs < observedLocalTimeUs ||
                monotonicFrequency <= 0L ||
                currentMonotonicTicks < observedMonotonicTicks)
            {
                return false;
            }

            long localElapsedUs = currentLocalTimeUs - observedLocalTimeUs;
            if (localElapsedUs > MaxServerTimeObservationAgeUs) return false;

            double monotonicElapsedUs =
                (currentMonotonicTicks - observedMonotonicTicks) *
                (1_000_000d / monotonicFrequency);
            return Math.Abs(localElapsedUs - monotonicElapsedUs) <=
                TimestampSafetyMarginUs;
        }
    }
}
