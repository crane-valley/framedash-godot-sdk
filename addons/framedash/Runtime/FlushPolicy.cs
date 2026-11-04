namespace Framedash
{
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

        public int EstimatePayloadBytes(int eventCount)
        {
            return eventCount * BytesPerEventEstimate;
        }

        public bool ShouldRequestFlush(int eventCount, int estimatedBytes)
        {
            return eventCount >= MaxBatchSize || estimatedBytes >= MaxPayloadBytes;
        }

        public bool ShouldFlush(bool flushRequested, float elapsedSeconds)
        {
            return flushRequested || elapsedSeconds >= FlushIntervalSeconds;
        }
    }
}
