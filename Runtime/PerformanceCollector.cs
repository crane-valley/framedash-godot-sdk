using Godot;

namespace Framedash
{
    /// <summary>
    /// Collects performance metrics each frame using Godot 4.x APIs.
    /// Call <see cref="UpdateFrameTimings"/> every frame (e.g. from a Node's
    /// _Process) so the cached GPU / CPU thread times and the real-time frame
    /// delta stay fresh. <see cref="Collect"/> reads the cached values without
    /// re-capturing.
    ///
    /// Fail-safe: every Godot call is wrapped; on failure the affected metric is
    /// set to 0 (the proto contract treats 0 as "not collected"). This class
    /// never throws -- game stability always wins over telemetry completeness.
    /// </summary>
    public sealed class PerformanceCollector
    {
        public struct PerfSnapshot
        {
            public float Fps;
            public float FrameTimeMs;
            public long MemoryUsedBytes;
            public float GpuTimeMs;
            public float GameThreadMs;
            public float RenderThreadMs;
            // Raw GPU-memory monitor readings (bytes); 0 if unavailable/uncollected.
            // MemMetricsBuilder decides omission (a raw 0 here never becomes a
            // stuffed mem.* metric).
            public double VramBytes;
            public double TexturesBytes;
            public double BuffersBytes;
        }

        // Real-time frame delta, computed from Time.GetTicksUsec() rather than
        // _Process(double delta). Godot scales the _Process delta by
        // Engine.TimeScale, but FPS / frame time must be time-scale-INDEPENDENT
        // (matches Unity's use of Time.unscaledDeltaTime), so we measure the
        // wall-clock delta ourselves. 0 on the first call (no prior sample).
        private ulong _lastTicksUsec;
        private float _frameTimeMs;
        // FPS derived from the RAW (unclamped) frame delta, capped to the ingest
        // ceiling (1000). Kept separate from _frameTimeMs so a long (>10s) frame
        // reports its true low rate rather than the clamped frame time's 0.1 fps.
        private float _fps;

        // Cached per-frame values from the rendering / performance monitors.
        private float _cachedGpuTimeMs;
        private float _cachedGameThreadMs;
        private float _cachedRenderThreadMs;
        // Memory sampled on the main thread in UpdateFrameTimings and cached, so
        // Collect() (which may run from a background Track() call) makes no native
        // Godot call off the main thread.
        private long _cachedMemoryUsedBytes;
        // GPU-memory monitors (mem.* metrics), sampled alongside MemoryStatic for the
        // same off-thread-safety reason. Kept as raw double bytes (Performance.GetMonitor's
        // native return type); MemMetricsBuilder applies the omit-when-<=0 and range clamp
        // rules when the heartbeat builds the metrics map.
        private double _cachedVramBytes;
        private double _cachedTexturesBytes;
        private double _cachedBuffersBytes;

        // Guards all cached fields: UpdateFrameTimings writes them on the main thread while
        // Collect may read them from a background Track() call. The lock keeps each snapshot
        // coherent and avoids torn reads of the 64-bit memory field on 32-bit platforms.
        private readonly object _lock = new object();
        // viewport_set_measure_render_time must be enabled before the measured GPU/CPU
        // render-time APIs return non-zero; enabled lazily on the first sample.
        private bool _measureEnabled;

        /// <summary>
        /// Sample the real-time frame delta and cache GPU / CPU thread times.
        /// Must be called once per frame so <see cref="Collect"/> always reads a
        /// recent sample. The viewport RID identifies the main viewport whose
        /// measured GPU / render-CPU times are read; pass it from the calling
        /// Node (e.g. GetViewport().GetViewportRid()).
        ///
        /// The GPU measurement is inherently delayed by a few frames (async GPU
        /// timing) and returns 0 until the renderer has a sample. Each metric is
        /// set to 0 if its source is unavailable or throws.
        /// </summary>
        public void UpdateFrameTimings(Rid viewportRid)
        {
            lock (_lock)
            {
                // Wall-clock frame delta (microseconds), independent of Engine.TimeScale.
                try
                {
                    ulong nowUs = Time.GetTicksUsec();
                    // First call has no prior sample -- report 0 delta rather than a
                    // bogus huge value computed from _lastTicksUsec == 0.
                    // Guard the first sample and any non-monotonic clock (nowUs < last)
                    // so the unsigned subtraction cannot underflow to a huge delta.
                    ulong realDeltaUs = (_lastTicksUsec == 0UL || nowUs < _lastTicksUsec) ? 0UL : nowUs - _lastTicksUsec;
                    float rawFrameTimeMs = realDeltaUs / 1000.0f;
                    // Clamp the frame_time field to the ingest ceiling (10000ms): a long
                    // pause/resume gap would otherwise emit a frame_time the validator
                    // rejects (dropping the whole batch).
                    _frameTimeMs = FieldClamp.ClampTimingMs(rawFrameTimeMs);
                    // Derive FPS from the RAW delta (only the high end is capped, to 1000),
                    // not the clamped frame time: a long (>10s) frame would otherwise report
                    // 0.1 fps instead of its true lower rate. fps down to 0 is valid to ingest.
                    _fps = FieldClamp.FpsFromFrameTimeMs(rawFrameTimeMs);
                    _lastTicksUsec = nowUs;
                }
                catch
                {
                    _frameTimeMs = 0f;
                    _fps = 0f;
                }

                // Godot only returns non-zero measured render times after measurement is
                // enabled for the viewport; enable it once (lazily, on the main thread).
                if (!_measureEnabled)
                {
                    try { RenderingServer.ViewportSetMeasureRenderTime(viewportRid, true); }
                    catch { /* leave disabled; GPU/render times stay 0 = not collected */ }
                    _measureEnabled = true;
                }

                // GPU render time for the main viewport (best-effort; ms).
                try
                {
                    float gpu = (float)RenderingServer.ViewportGetMeasuredRenderTimeGpu(viewportRid);
                    // Guard NaN / negatives (renderer not yet measured) -> 0 = "not collected";
                    // cap at the ingest GPU-time ceiling (10000ms) so a spike cannot drop a batch.
                    _cachedGpuTimeMs = FieldClamp.ClampTimingMs(gpu);
                }
                catch
                {
                    _cachedGpuTimeMs = 0f;
                }

                // CPU-side render time for the main viewport (the render thread; ms).
                try
                {
                    float render = (float)RenderingServer.ViewportGetMeasuredRenderTimeCpu(viewportRid);
                    _cachedRenderThreadMs = FieldClamp.ClampTimingMs(render);
                }
                catch
                {
                    _cachedRenderThreadMs = 0f;
                }

                // Main-thread process time. Performance.Monitor.TimeProcess is reported
                // in seconds, so convert to milliseconds to match the GPU / render units.
                try
                {
                    float game = (float)(Performance.GetMonitor(Performance.Monitor.TimeProcess) * 1000.0);
                    _cachedGameThreadMs = FieldClamp.ClampTimingMs(game);
                }
                catch
                {
                    _cachedGameThreadMs = 0f;
                }

                // Godot's tracked STATIC memory (Performance.Monitor.MemoryStatic), not the
                // full process RSS -- analogous to Unity's Profiler.GetTotalAllocatedMemoryLong().
                // Sampled here on the main thread (alongside the other native probes) so the
                // off-thread Track() -> Collect() path reads only a cached primitive.
                try
                {
                    long mem = (long)Performance.GetMonitor(Performance.Monitor.MemoryStatic);
                    // Clamp to the ingest range [0, 64 GiB]: a negative/garbage reading or
                    // an oversized value would otherwise be rejected, dropping the batch.
                    _cachedMemoryUsedBytes = FieldClamp.ClampMemory(mem);
                }
                catch
                {
                    _cachedMemoryUsedBytes = 0L;
                }

                // GPU-memory monitors: video/texture/buffer usage in bytes. Each is sampled
                // independently (one failing must not blank the others) and left as raw 0
                // on failure/unavailability -- MemMetricsBuilder treats <= 0 as "not
                // collected" and omits the key rather than emitting a stuffed 0.
                try { _cachedVramBytes = Performance.GetMonitor(Performance.Monitor.RenderVideoMemUsed); }
                catch { _cachedVramBytes = 0.0; }

                try { _cachedTexturesBytes = Performance.GetMonitor(Performance.Monitor.RenderTextureMemUsed); }
                catch { _cachedTexturesBytes = 0.0; }

                try { _cachedBuffersBytes = Performance.GetMonitor(Performance.Monitor.RenderBufferMemUsed); }
                catch { _cachedBuffersBytes = 0.0; }
            }
        }

        /// <summary>Collect current frame performance data using cached timings.</summary>
        public PerfSnapshot Collect()
        {
            // Memory was sampled on the main thread in UpdateFrameTimings and cached, so
            // this method (which may run from a background Track() call) makes no native
            // Godot call off the main thread. The lock keeps the snapshot coherent.
            lock (_lock)
            {
                return new PerfSnapshot
                {
                    // FPS derived from the real-time (unscaled) RAW frame delta in
                    // UpdateFrameTimings, capped to the ingest ceiling (1000).
                    Fps = _fps,
                    FrameTimeMs = _frameTimeMs,
                    MemoryUsedBytes = _cachedMemoryUsedBytes,
                    GpuTimeMs = _cachedGpuTimeMs,
                    GameThreadMs = _cachedGameThreadMs,
                    RenderThreadMs = _cachedRenderThreadMs,
                    VramBytes = _cachedVramBytes,
                    TexturesBytes = _cachedTexturesBytes,
                    BuffersBytes = _cachedBuffersBytes,
                };
            }
        }
    }
}
