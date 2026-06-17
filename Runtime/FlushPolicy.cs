namespace Framedash
{
    /// <summary>
    /// Pure flush decision logic extracted from TelemetrySDK.
    /// No engine dependencies -- testable with NUnit.
    /// </summary>
    public sealed class FlushPolicy
    {
        public int MaxBatchSize { get; }
        public int MaxPayloadBytes { get; }
        public float FlushIntervalSeconds { get; }
        public int BytesPerEventEstimate { get; }

        public FlushPolicy(
            int maxBatchSize = 100,
            int maxPayloadBytes = 102400,
            float flushIntervalSeconds = 30f,
            int bytesPerEventEstimate = 500)
        {
            MaxBatchSize = maxBatchSize > 0 ? maxBatchSize : 100;
            MaxPayloadBytes = maxPayloadBytes > 0 ? maxPayloadBytes : 102400;
            FlushIntervalSeconds = flushIntervalSeconds > 0f ? flushIntervalSeconds : 30f;
            BytesPerEventEstimate = bytesPerEventEstimate > 0 ? bytesPerEventEstimate : 500;
        }

        /// <summary>
        /// Estimate the payload size in bytes for the given event count.
        /// </summary>
        public int EstimatePayloadBytes(int eventCount)
        {
            return eventCount * BytesPerEventEstimate;
        }

        /// <summary>
        /// Whether a flush should be requested immediately after Track().
        /// Triggered when event count reaches batch size or estimated payload reaches limit.
        /// </summary>
        public bool ShouldRequestFlush(int eventCount, int estimatedBytes)
        {
            return eventCount >= MaxBatchSize || estimatedBytes >= MaxPayloadBytes;
        }

        /// <summary>
        /// Whether the flush loop should trigger a flush based on time elapsed
        /// or a pending flush request.
        /// </summary>
        public bool ShouldFlush(bool flushRequested, float elapsedSeconds)
        {
            return flushRequested || elapsedSeconds >= FlushIntervalSeconds;
        }
    }
}
