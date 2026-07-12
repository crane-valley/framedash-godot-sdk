using System;

namespace Framedash
{
    /// <summary>
    /// Immutable snapshot of one drained I/O accumulation window. EverActive is
    /// sticky across drains (true forever once ReportIoSample has been called at
    /// least once), so a heartbeat can decide whether to attach io.* keys at all
    /// even during a quiet window with zero bytes -- matches the ingest map
    /// semantics of "absent key = not collected" (no 0-stuffing).
    /// </summary>
    public readonly struct IoWindow
    {
        public readonly long ReadBytes;
        public readonly float ReadTimeMs;
        public readonly long ReadOps;
        public readonly bool EverActive;

        public IoWindow(long readBytes, float readTimeMs, long readOps, bool everActive)
        {
            ReadBytes = readBytes;
            ReadTimeMs = readTimeMs;
            ReadOps = readOps;
            EverActive = everActive;
        }
    }

    /// <summary>
    /// Thread-safe accumulator for manually-fed disk I/O samples. Godot exposes no
    /// engine-level I/O counters, so the Godot SDK is manual-feed only (io.* Phase 1
    /// design, PLANS.md "Storage/disk I/O metrics capture"): the game calls
    /// ReportIoSample as it performs its own reads, and TelemetrySDK drains the
    /// accumulated window into the perf_heartbeat metrics map.
    ///
    /// Add() may be called from any thread (loader/worker threads); DrainWindow()
    /// is called from the heartbeat path, which -- like PerformanceCollector.Collect()
    /// in this SDK -- may itself run off the main thread, so both methods share one
    /// lock rather than relying on call-site threading assumptions.
    /// </summary>
    public sealed class IoStats
    {
        private readonly object _lock = new object();
        private long _bytes;
        private double _readTimeMs;
        private long _ops;
        private bool _everActive;

        /// <summary>
        /// Accumulate one I/O sample. Never throws (callers -- TelemetrySDK's public
        /// ReportIoSample -- also wrap in try/catch per the SDK's fail-safe
        /// convention, but the accumulator itself has no throwing path).
        /// Non-finite or negative inputs are dropped in full (not clamped to 0):
        /// a single bad sample simply does not contribute, rather than silently
        /// zero-padding the window with a partial/garbage reading.
        /// </summary>
        public void Add(long bytes, float readTimeMs, int ops)
        {
            if (bytes < 0L) return;
            if (float.IsNaN(readTimeMs) || float.IsInfinity(readTimeMs) || readTimeMs < 0f) return;
            if (ops < 0) return;

            lock (_lock)
            {
                // Saturating adds: extreme (but individually valid) samples must clamp
                // at long.MaxValue instead of wrapping negative, which would publish a
                // nonsensical negative io.read_bytes/io.read_ops on the next heartbeat.
                _bytes = long.MaxValue - _bytes < bytes ? long.MaxValue : _bytes + bytes;
                _readTimeMs += readTimeMs;
                _ops = long.MaxValue - _ops < ops ? long.MaxValue : _ops + ops;
                _everActive = true;
            }
        }

        /// <summary>
        /// Snapshot the accumulated totals since the previous drain (or since
        /// construction) and reset the running totals to zero. EverActive on the
        /// returned window reflects whether ANY sample has ever been accepted, not
        /// just this window, so it stays true after the first successful Add() even
        /// through zero-activity drains.
        /// </summary>
        public IoWindow DrainWindow()
        {
            lock (_lock)
            {
                // Saturate before narrowing: two individually finite float samples can
                // sum past float.MaxValue in the double accumulator, and a bare cast
                // would then yield +Infinity -- which ingest's finite-metric validation
                // rejects, dropping the whole batch. Clamp to float.MaxValue instead.
                var readTimeMs = (float)Math.Min(_readTimeMs, float.MaxValue);
                var window = new IoWindow(_bytes, readTimeMs, _ops, _everActive);
                _bytes = 0L;
                _readTimeMs = 0.0;
                _ops = 0L;
                return window;
            }
        }
    }
}
