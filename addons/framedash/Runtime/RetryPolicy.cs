using System;

namespace Framedash
{
    /// <summary>
    /// Pure retry decision logic extracted from TransportLayer.
    /// No engine dependencies -- testable with NUnit.
    /// </summary>
    public sealed class RetryPolicy
    {
        // Tightened by the 2026-07-02 dogfood fix (F49). Godot/.NET HttpClient has no
        // Happy Eyeballs, so an IPv6 blackhole wedges the connect for the full HTTP
        // timeout, and the SDK holds its single-flight flush guard across the ENTIRE
        // retry ladder. A short budget (3 attempts) and a short per-attempt timeout
        // (10s) bound the worst-case flush wall-time to ~33s (see
        // WorstCaseTotalSeconds), so a wedged endpoint degrades to "slow but
        // delivering" instead of silent total data loss: later flushes (perf
        // heartbeats, session_end, the shutdown flush) are not starved, and the
        // "Dropping N events" log is reachable within a realistic session.
        public const int DefaultMaxRetries = 3;
        public const float DefaultBaseDelaySeconds = 1f;
        public const float DefaultHttpTimeoutSeconds = 10f;

        public int MaxRetries { get; }
        public float BaseDelaySeconds { get; }

        /// <summary>
        /// Per-attempt HTTP timeout (seconds). Owned here so the worst-case flush
        /// wall-time is a pure, testable property of the retry configuration; the
        /// transport applies it to its HttpRequest node.
        /// </summary>
        public float HttpTimeoutSeconds { get; }

        /// <summary>
        /// Worst-case total flush wall-time (seconds) when EVERY attempt blocks for the
        /// full HTTP timeout -- the wedged-endpoint case. The sum of each attempt's
        /// timeout plus the exponential backoff between attempts (no backoff after the
        /// last attempt). This is the window the single-flight guard is held for, so it
        /// must stay small (target ~30-40s) to avoid starving later flushes.
        ///
        /// It is also the DEADLINE the transport shares across a split flush: when a
        /// drained batch is split (wire caps / payload size) each child would otherwise
        /// get its own full retry ladder, so a wedged endpoint with N chunks would hold
        /// the guard for N x this value. The transport instead stops retrying once this
        /// budget is spent for the whole flush and caps each request's timeout to the
        /// remaining budget (see <see cref="RemainingBudgetSeconds"/> and
        /// <see cref="EffectiveRequestTimeoutSeconds"/>), so the bound is honored exactly
        /// regardless of split depth.
        ///
        /// Config is immutable, so this is computed once in the constructor.
        /// </summary>
        public float WorstCaseTotalSeconds { get; }

        public RetryPolicy(int maxRetries = DefaultMaxRetries,
            float baseDelaySeconds = DefaultBaseDelaySeconds,
            float httpTimeoutSeconds = DefaultHttpTimeoutSeconds)
        {
            MaxRetries = maxRetries > 0 ? maxRetries : DefaultMaxRetries;
            BaseDelaySeconds = baseDelaySeconds > 0f ? baseDelaySeconds : DefaultBaseDelaySeconds;
            HttpTimeoutSeconds = httpTimeoutSeconds > 0f ? httpTimeoutSeconds : DefaultHttpTimeoutSeconds;
            WorstCaseTotalSeconds = ComputeWorstCaseTotalSeconds();
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

        // Compute the worst-case ladder time once (config is immutable). Called from the
        // constructor; avoids a Math.Pow loop on every per-attempt budget check.
        private float ComputeWorstCaseTotalSeconds()
        {
            float total = 0f;
            for (int attempt = 0; attempt < MaxRetries; attempt++)
            {
                total += HttpTimeoutSeconds;
                if (attempt + 1 < MaxRetries)
                    total += GetRetryDelaySeconds(attempt);
            }
            return total;
        }

        /// <summary>
        /// Whether a flush that has already spent <paramref name="elapsedSeconds"/> of
        /// wall-time still has budget left to make another delivery attempt. The budget
        /// is <see cref="WorstCaseTotalSeconds"/> and is SHARED across all split
        /// children of one flush, so a wedged endpoint cannot multiply the starvation
        /// window by the number of chunks. A non-positive elapsed always has budget.
        /// </summary>
        public bool HasBudgetRemaining(float elapsedSeconds)
        {
            return elapsedSeconds < WorstCaseTotalSeconds;
        }

        /// <summary>
        /// Remaining shared flush budget (seconds), clamped to >= 0. Used to cap each
        /// request's timeout so a request started near the deadline cannot overshoot it
        /// by a full <see cref="HttpTimeoutSeconds"/>.
        /// </summary>
        public float RemainingBudgetSeconds(float elapsedSeconds)
        {
            float remaining = WorstCaseTotalSeconds - elapsedSeconds;
            return remaining > 0f ? remaining : 0f;
        }

        /// <summary>
        /// The timeout to apply to the NEXT request: the per-attempt
        /// <see cref="HttpTimeoutSeconds"/> capped to the remaining shared flush budget,
        /// so the whole flush (including split children) never exceeds
        /// <see cref="WorstCaseTotalSeconds"/> even if the last request starts just
        /// before the deadline.
        /// </summary>
        public float EffectiveRequestTimeoutSeconds(float elapsedSeconds)
        {
            float remaining = RemainingBudgetSeconds(elapsedSeconds);
            return remaining < HttpTimeoutSeconds ? remaining : HttpTimeoutSeconds;
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
