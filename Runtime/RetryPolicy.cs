using System;

namespace Framedash
{
    /// <summary>
    /// Pure retry decision logic extracted from TransportLayer.
    /// No engine dependencies -- testable with NUnit.
    /// </summary>
    public sealed class RetryPolicy
    {
        public int MaxRetries { get; }
        public float BaseDelaySeconds { get; }

        public RetryPolicy(int maxRetries = 5, float baseDelaySeconds = 1f)
        {
            MaxRetries = maxRetries > 0 ? maxRetries : 5;
            BaseDelaySeconds = baseDelaySeconds > 0f ? baseDelaySeconds : 1f;
        }

        /// <summary>
        /// Whether the response warrants splitting the batch in half.
        /// Only applies to HTTP 413 with more than one event.
        /// </summary>
        public bool ShouldSplitBatch(long httpStatusCode, int eventCount)
        {
            return httpStatusCode == 413 && eventCount > 1;
        }

        /// <summary>
        /// Whether the response is a non-retryable client error.
        /// 4xx except 413 (split) and 429 (rate limit, retryable).
        /// </summary>
        public bool IsNonRetryableError(long httpStatusCode)
        {
            return httpStatusCode >= 400 && httpStatusCode < 500
                && httpStatusCode != 413
                && httpStatusCode != 429;
        }

        /// <summary>
        /// Whether a specific status code is known-retryable (5xx, 429, network error).
        /// Internal: production code should use <see cref="Classify"/>.
        /// </summary>
        internal bool ShouldRetry(long httpStatusCode, int attempt)
        {
            if (attempt >= MaxRetries) return false;

            // Network error / timeout (status 0)
            if (httpStatusCode == 0) return true;

            // 429 rate limit
            if (httpStatusCode == 429) return true;

            // 5xx server error
            if (httpStatusCode >= 500) return true;

            return false;
        }

        /// <summary>
        /// Exponential backoff delay for the given attempt (0-based).
        /// Returns BaseDelaySeconds * 2^attempt.
        /// </summary>
        public float GetRetryDelaySeconds(int attempt)
        {
            if (attempt < 0) attempt = 0;
            return BaseDelaySeconds * (float)Math.Pow(2, attempt);
        }

        /// <summary>
        /// Classify the HTTP response into an action the transport layer should take.
        /// </summary>
        public RetryAction Classify(long httpStatusCode, int attempt, int eventCount)
        {
            if (httpStatusCode >= 200 && httpStatusCode < 300)
                return RetryAction.Success;

            if (ShouldSplitBatch(httpStatusCode, eventCount))
                return RetryAction.SplitBatch;

            if (IsNonRetryableError(httpStatusCode))
                return RetryAction.Fail;

            // 413 with unsplittable single event -- can't split, can't retry
            if (httpStatusCode == 413)
                return RetryAction.Fail;

            // 3xx: HttpRequest.MaxRedirects=0 means redirects are never
            // followed, so a surfaced 3xx indicates a misconfigured or
            // compromised endpoint. Retrying cannot succeed -- fail immediately
            // so the error surfaces rather than consuming the full retry budget
            // on every batch. Mirrors UE5 FRetryPolicy behavior exactly.
            if (httpStatusCode >= 300 && httpStatusCode < 400)
                return RetryAction.Fail;

            // Everything else (5xx, 429, network errors with status 0, 1xx)
            // retries until the attempt budget is exhausted.
            if (attempt >= MaxRetries)
                return RetryAction.Fail;

            return RetryAction.Retry;
        }
    }

    public enum RetryAction
    {
        Success,
        Retry,
        SplitBatch,
        Fail,
    }
}
