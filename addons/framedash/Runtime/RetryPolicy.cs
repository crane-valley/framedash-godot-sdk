using System;

namespace Framedash
{
    // Split children share this latch so an unavailable clock endpoint cannot consume
    // a fresh bootstrap allowance at every recursion depth and starve the telemetry POST.
    internal sealed class ServerTimeBootstrapState
    {
        private bool _started;

        internal float StartedAtSeconds { get; private set; }

        internal bool TryStart(float elapsedSeconds)
        {
            if (_started) return false;
            _started = true;
            StartedAtSeconds = elapsedSeconds;
            return true;
        }
    }

    public sealed class RetryPolicy
    {
        // Godot/.NET HttpClient has no Happy Eyeballs, so an IPv6 blackhole wedges the connect
        // for the full HTTP timeout, and the SDK holds its single-flight flush guard across the
        // ENTIRE retry ladder. A short budget (3 attempts) and a short per-attempt timeout
        // (10s) bound the worst-case flush wall-time to ~33s (see WorstCaseTotalSeconds), so a
        // wedged endpoint degrades to "slow but delivering" instead of silent total data loss:
        // later flushes (perf heartbeats, session_end, the shutdown flush) are not starved, and
        // the "Dropping N events" log is reachable within a realistic session.
        public const int DefaultMaxRetries = 3;
        public const float DefaultBaseDelaySeconds = 1f;
        public const float DefaultHttpTimeoutSeconds = 10f;
        public const float ServerTimeBootstrapTimeoutSeconds = 0.5f;

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

        public bool ShouldSplitBatch(long httpStatusCode, int eventCount)
        {
            return httpStatusCode == 413 && eventCount > 1;
        }

        public bool IsNonRetryableError(long httpStatusCode)
        {
            return httpStatusCode >= 400 && httpStatusCode < 500
                && httpStatusCode != 413
                && httpStatusCode != 429;
        }

        /// <summary>
        /// Whether an HTTP status is a PERMANENT rejection that re-sending can never fix, so
        /// the batch must be DROPPED (never requeued) rather than retried forever: a 3xx
        /// (redirects are not followed, so a surfaced 3xx is a misconfigured/compromised
        /// endpoint) or a non-retryable 4xx (400/401/403/404 ...). Deliberately EXCLUDES 413
        /// (splitting can help -- the async path splits a multi-event 413 and only drops a
        /// genuinely unsplittable single event) and 429 (rate limit) and 5xx / network status
        /// 0 (all transient). This is the drop-vs-requeue analogue of <see cref="Classify"/>
        /// for the no-retry blocking drain, where there is no attempt counter.
        /// </summary>
        public bool IsPermanentRejection(long httpStatusCode)
        {
            return (httpStatusCode >= 300 && httpStatusCode < 400)
                || IsNonRetryableError(httpStatusCode);
        }

        /// <summary>
        /// Permanent-rejection decision for a CHUNK whose splittability is known -- used by
        /// the blocking drain, which (unlike the async retry loop) has no split-on-413 step.
        /// A 413 is PERMANENT only when the chunk cannot be split any further
        /// (<paramref name="canSplit"/> == false, i.e. a single event): re-sending the same
        /// oversized event can never succeed, so it must be dropped rather than requeued
        /// forever. A 413 that can still split is transient (requeue -> the async path splits
        /// it). This mirrors <see cref="Classify"/> exactly, where a 413 with more than one
        /// event returns SplitBatch and a single-event 413 returns Fail. Non-413 codes are
        /// classified by <see cref="IsPermanentRejection"/> regardless of splittability.
        /// </summary>
        public bool IsPermanentRejectionForChunk(long httpStatusCode, bool canSplit)
        {
            if (httpStatusCode == 413) return !canSplit;
            return IsPermanentRejection(httpStatusCode);
        }

        /// <summary>
        /// Whether a blocking send that hit its deadline should still READ the response
        /// instead of concluding a transient timeout: true when the final poll left the
        /// request COMPLETED with a response available. Reading an already-completed
        /// response is bounded work (no further polling), and skipping it would classify
        /// a server-accepted batch as transient -- the requeue would then duplicate every
        /// event (insert + charge) on the next flush. Pure so the boundary decision is
        /// NextUnit-testable; the engine supplies the two observations.
        /// </summary>
        public static bool ShouldReadCompletedResponseAtDeadline(bool requestCompleted, bool hasResponse)
        {
            return requestCompleted && hasResponse;
        }

        /// <summary>
        /// The next blocking-poll sleep (ms): the poll interval capped to the remaining
        /// budget, so the wait loop can never sleep past the deadline -- a fixed sleep
        /// would overshoot the documented budget whenever timeoutMs is smaller than the
        /// interval or the deadline is nearer than one interval. Non-positive remaining
        /// budget (or interval) yields 0 (no sleep).
        /// </summary>
        public static int NextPollDelayMs(long remainingBudgetMs, int pollIntervalMs)
        {
            if (remainingBudgetMs <= 0 || pollIntervalMs <= 0) return 0;
            return remainingBudgetMs < pollIntervalMs ? (int)remainingBudgetMs : pollIntervalMs;
        }

        internal bool ShouldRetry(long httpStatusCode, int attempt)
        {
            if (attempt >= MaxRetries) return false;

            if (httpStatusCode == 0) return true;

            if (httpStatusCode == 429) return true;

            if (httpStatusCode >= 500) return true;

            return false;
        }

        public float GetRetryDelaySeconds(int attempt)
        {
            if (attempt < 0) attempt = 0;
            return BaseDelaySeconds * (float)Math.Pow(2, attempt);
        }

        // Called from the constructor; avoids a Math.Pow loop on every per-attempt budget
        // check.
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
        /// The budget is `WorstCaseTotalSeconds` and is SHARED across all split children of one
        /// flush, so a wedged endpoint cannot multiply the starvation window by the number of
        /// chunks. A non-positive elapsed always has budget.
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
        /// Timeout for the optional server-clock HEAD request. This bootstrap must
        /// consume only a small part of the shared flush budget so even a one-attempt
        /// configuration retains time for the telemetry POST.
        /// </summary>
        public float EffectiveBootstrapTimeoutSeconds(
            float elapsedSeconds,
            float bootstrapStartedAtSeconds)
        {
            float bootstrapAllowance =
                RemainingBudgetSeconds(bootstrapStartedAtSeconds);
            if (bootstrapAllowance > ServerTimeBootstrapTimeoutSeconds)
                bootstrapAllowance = ServerTimeBootstrapTimeoutSeconds;
            float remainingBootstrap =
                bootstrapStartedAtSeconds + bootstrapAllowance - elapsedSeconds;
            if (remainingBootstrap <= 0f) return 0f;

            float requestTimeout = EffectiveRequestTimeoutSeconds(elapsedSeconds);
            return requestTimeout < remainingBootstrap
                ? requestTimeout
                : remainingBootstrap;
        }

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
