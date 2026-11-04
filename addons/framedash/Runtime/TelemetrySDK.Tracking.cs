#nullable disable

using System;
using System.Threading;
using System.Collections.Generic;
using Godot;

namespace Framedash
{
    public partial class TelemetrySDK : Node
    {
        public void Track(string eventName, string mapId = "",
            Vector3? position = null, Dictionary<string, string> attributes = null,
            Dictionary<string, float> metrics = null)
        {
            try
            {
                if (!_initialized)
                {
                    GD.PushWarning("[Framedash] SDK not initialized. Call Initialize() first.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(eventName))
                {
                    GD.PushWarning("[Framedash] eventName must not be null, empty, or whitespace. Event dropped.");
                    return;
                }

                if (!_warnedEmptyPlayerId && string.IsNullOrEmpty(_session.PlayerId))
                {
                    _warnedEmptyPlayerId = true;
                    GD.PushWarning("[Framedash] No player_id set. Events will be sent as anonymous. Call SetPlayerId() to associate events with a player.");
                }

                // Normalize event name first so sampling and the wire-side event use the
                // same key — overrides registered for long names must match the truncated
                // form that actually leaves the SDK and that ingest validation accepts.
                string safeEventName = FieldClamp.TruncateEventName(eventName);

                // Sampling check — skip expensive perf collection if event is dropped
                if (!_samplingPolicy.ShouldSample(safeEventName))
                    return;

                // Convert Dictionary parameters to serializable List types, enforcing the
                // ingest-core caps client-side (count, key/value length, finite metrics) so a
                // single oversized map cannot make the consumer drop the whole flush.
                List<StringPair> attrList = FieldClamp.ClampAttributes(attributes);
                List<FloatPair> metricList = FieldClamp.ClampMetrics(metrics);

                TrackInternal(
                    safeEventName,
                    FieldClamp.Truncate(mapId ?? "", FieldClamp.MaxMapIdLength),
                    FieldClamp.SanitizeCoord(position?.X ?? 0f),
                    FieldClamp.SanitizeCoord(position?.Y ?? 0f),
                    FieldClamp.SanitizeCoord(position?.Z ?? 0f),
                    TelemetrySource.Player,
                    attrList,
                    metricList);
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] Track() failed: {e.Message}");
            }
        }

        private const string PerfHeartbeatEventName = "perf_heartbeat";

        private void TrackAutomated(string eventName)
        {
            try
            {
                // Automated events (session_start, perf_heartbeat) bypass sampling,
                // name validation, and player-ID checks — they are always valid and
                // fired from internal SDK code after initialization succeeds.
                //
                // io.* disk-I/O metrics attach ONLY to perf_heartbeat, and only once
                // the manual feed (ReportIoSample) has ever been used -- an unused
                // feed keeps metrics null (absent = not collected; no 0-stuffing).
                // The window is drained here (resetting it) regardless of whether the
                // event ends up sampled/buffered, matching "window since the previous
                // heartbeat" even if a caller invokes TrackAutomated more than once.
                List<FloatPair> metrics = null;
                if (eventName == PerfHeartbeatEventName)
                {
                    var window = _ioStats.DrainWindow();
                    if (window.EverActive)
                    {
                        // Allocated fresh per heartbeat (not reused): the buffer this
                        // list becomes attaches to a struct copied into the ring
                        // buffer, which can sit unflushed alongside other heartbeats
                        // for multiple intervals, so a shared/reused list would let a
                        // later drain mutate an event still waiting to be serialized.
                        // Heap allocation here is allowed under the SDK's convention
                        // (metrics-carrying events, not the per-frame/allocation-free
                        // path) at the ~10s heartbeat cadence.
                        metrics = new List<FloatPair>(3)
                        {
                            new FloatPair("io.read_bytes", window.ReadBytes),
                            new FloatPair("io.read_time_ms", window.ReadTimeMs),
                            new FloatPair("io.read_ops", window.ReadOps),
                        };
                    }
                    // mem.* attach is handled centrally in TrackInternal (it always fires
                    // for perf_heartbeat, and also for any position-qualified event), so
                    // it is not duplicated here.
                }

                TrackInternal(
                    eventName,
                    mapId: "",
                    posX: 0f, posY: 0f, posZ: 0f,
                    source: TelemetrySource.Automated,
                    attributes: null,
                    metrics: metrics);
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] TrackAutomated({eventName}) failed: {e.Message}");
            }
        }

        /// <summary>
        /// Manually report one disk I/O sample (bytes read, time spent, op count) for
        /// accumulation into the next perf_heartbeat's io.* metrics. Godot exposes no
        /// engine-level I/O counters, so this SDK is manual-feed only: call this from
        /// custom loaders / VFS code as reads complete (see README for an example
        /// around ResourceLoader.LoadThreadedRequest polling). Safe to call from any
        /// thread. Never throws into game code. Non-finite or negative arguments are
        /// dropped (the sample does not contribute) rather than clamped, so one bad
        /// reading cannot masquerade as a zero-cost read. No-op if the SDK is not
        /// initialized.
        /// </summary>
        public void ReportIoSample(long bytes, float readTimeMs, int ops)
        {
            try
            {
                if (!_initialized) return;
                _ioStats.Add(bytes, readTimeMs, ops);
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] ReportIoSample() failed: {e.Message}");
            }
        }

        // Monotonic wall-clock seconds from a high-resolution timer. Unaffected by
        // Engine.TimeScale or a paused tree (unlike _Process's scaled delta), so a
        // load measured across a pause or slow-motion is still real elapsed time.
        private static double MonotonicSeconds()
            => (double)System.Diagnostics.Stopwatch.GetTimestamp()
                / System.Diagnostics.Stopwatch.Frequency;

        /// <summary>
        /// Begin timing a map/level load. Records <paramref name="mapName"/> and a
        /// monotonic start timestamp; call <see cref="EndMapLoad"/> when loading
        /// completes to emit a <c>map_load</c> event whose <c>map_id</c> is deliberately
        /// EMPTY (keeping it out of the spatial heatmap grid and the activation gate); the
        /// map name rides <c>attributes["map_name"]</c> and the elapsed time rides the
        /// <c>metrics["load_time_ms"]</c> metric. The clock is
        /// wall-time monotonic (time-scale / pause safe). Calling BeginMapLoad again
        /// before EndMapLoad REPLACES the pending measurement (the earlier one is
        /// discarded). Safe to call from any thread. Never throws into game code. No-op
        /// if the SDK is not initialized.
        /// </summary>
        public void BeginMapLoad(string mapName)
        {
            try
            {
                if (!_initialized) return;
                _mapLoadTimer.Begin(mapName, MonotonicSeconds());
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] BeginMapLoad() failed: {e.Message}");
            }
        }

        public void EndMapLoad()
        {
            try
            {
                if (!_initialized) return;
                if (!_mapLoadTimer.End(MonotonicSeconds(), out string mapName, out double elapsedMs))
                    return;
                TrackMapLoad(mapName, elapsedMs);
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] EndMapLoad() failed: {e.Message}");
            }
        }

        public void ReportMapLoad(string mapName, double loadTimeMs)
        {
            try
            {
                if (!_initialized) return;
                if (!MapLoadTimer.IsValidLoadTimeMs(loadTimeMs)) return;
                TrackMapLoad(mapName, loadTimeMs);
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] ReportMapLoad() failed: {e.Message}");
            }
        }

        // Emit the map_load event through the public Track path so it inherits sampling,
        // field/attribute clamping (finite-metric drop, attribute-value truncation),
        // buffering, and CI session attributes -- it is a regular event, not a heartbeat.
        // map_id is left EMPTY (like perf_heartbeat) so this non-spatial event never lands
        // in the spatial heatmap grid query or the activation gate (both key on a non-empty
        // map_id); the loaded map name rides attributes["map_name"] instead, clamped to the
        // attribute-value cap by ClampAttributes. The load time rides metrics as
        // load_time_ms (no proto/CH field). A finite double that overflows float range
        // narrows to Infinity, which ClampMetrics would drop; skip it here so a map_load
        // without its load_time_ms metric is never emitted.
        private void TrackMapLoad(string mapName, double loadTimeMs)
        {
            float ms = (float)loadTimeMs;
            if (float.IsInfinity(ms)) return;
            var attributes = new Dictionary<string, string>(1) { { MapLoadTimer.KeyMapName, mapName ?? "" } };
            var metrics = new Dictionary<string, float>(1) { { MapLoadTimer.KeyLoadTimeMs, ms } };
            Track(MapLoadTimer.MapLoadEventName, mapId: "", attributes: attributes, metrics: metrics);
        }

        private void TrackInternal(
            string eventName,
            string mapId,
            float posX, float posY, float posZ,
            TelemetrySource source,
            List<StringPair> attributes,
            List<FloatPair> metrics)
        {
            var perf = _perfCollector.Collect();

            // mem.* GPU-memory metrics (mem.vram / mem.textures / mem.buffers) attach to
            // perf_heartbeat (as before) AND to any position-qualified event (non-empty
            // map_id) -- the spatial heatmap grid query keys on map_id + cell bounds, so a
            // heartbeat-only attach (empty map_id, no position) is invisible to it.
            //
            // perf_heartbeat is the ONLY refresh point: it rebuilds the mem.* list fresh
            // from the current PerfSnapshot and REPLACES _cachedMemMetrics (never mutates
            // the previous instance -- see MemMetricsBuilder.AttachTo for why). Every other
            // position-qualified event reuses that same cached reference instead of
            // resampling/reallocating per event, which would violate the allocation-
            // discipline hard rule on the spatial telemetry hot path. This means a
            // position-qualified event's mem.* values can be up to one heartbeat interval
            // (HeartbeatIntervalSeconds) stale -- acceptable, matching the Unity/UE5
            // cached-sample semantics for per-frame performance fields. Before the first
            // heartbeat has ever run, _cachedMemMetrics is null and mem.* is simply absent.
            if (eventName == PerfHeartbeatEventName)
            {
                _cachedMemMetrics = MemMetricsBuilder.Build(perf.VramBytes, perf.TexturesBytes, perf.BuffersBytes);
            }
            if (eventName == PerfHeartbeatEventName || !string.IsNullOrEmpty(mapId))
            {
                metrics = MemMetricsBuilder.AttachTo(metrics, _cachedMemMetrics);
            }

            float? camYaw = null;
            float? camPitch = null;
            if (_captureCameraRotation
                && CameraMath.TryUnpackCamera(
                    Interlocked.Read(ref _cameraSnapshot), out float unpackedYaw, out float unpackedPitch))
            {
                camYaw = unpackedYaw;
                camPitch = unpackedPitch;
            }

            // Resolve the CI session against this event from a SINGLE snapshot read, so the
            // stamped build_id and the merged ci.* attributes are always mutually consistent
            // even if Begin/EndAutomatedSession runs on the main thread while this Track()
            // executes on a background thread.
            var ciStamp = _session.ResolveSessionStamp(BuildId, attributes);

            long timestampUs =
                (DateTimeOffset.UtcNow.Ticks - 621355968000000000L) / 10L;
            long estimatedServerNowUs = _transport.EstimateServerNowUs(timestampUs);
            var evt = new TelemetryEvent
            {
                EventName = eventName,
                TimestampUs = GodotRuntimePolicy.ClampTimestampUs(
                    timestampUs,
                    estimatedServerNowUs),
                SessionId = _session.SessionId,
                PlayerId = _session.PlayerId,
                PositionX = posX,
                PositionY = posY,
                PositionZ = posZ,
                MapId = mapId,
                Fps = perf.Fps,
                FrameTimeMs = perf.FrameTimeMs,
                MemoryUsedBytes = perf.MemoryUsedBytes,
                GpuTimeMs = perf.GpuTimeMs,
                Source = source,
                // The automated-session build_id override (CI) when active, else the
                // configured BuildId -- resolved above. The public BuildId is never
                // overwritten, so a re-init or a direct BuildId change can never strand a
                // candidate id.
                BuildId = FieldClamp.Truncate(ciStamp.BuildId ?? "", FieldClamp.MaxBuildIdLength),
                Platform = _cachedPlatform,
                EngineVersion = _cachedEngineVersion,
                // The active automated-session attributes (CI metadata) merged with the
                // per-event ones -- from the same snapshot as BuildId -- so every event,
                // including the perf_heartbeat that feeds perf-diff, is tagged. No session
                // active -> the per-event list unchanged.
                Attributes = ciStamp.Attributes,
                Metrics = metrics,
                GameThreadMs = perf.GameThreadMs,
                RenderThreadMs = perf.RenderThreadMs,
                CameraYaw = camYaw,
                CameraPitch = camPitch,
            };

            _buffer.Enqueue(evt);

            // Estimate payload size for flush threshold check.
            // Flag a flush when batch size or payload threshold is reached.
            // The actual flush is deferred to _Process so it always runs on the
            // main (engine) thread, where Godot node/HTTP calls are safe.
            var currentBytes = Interlocked.Add(
                ref _estimatedPayloadBytes, _flushPolicy.BytesPerEventEstimate);
            if (_flushPolicy.ShouldRequestFlush(_buffer.Count, currentBytes))
            {
                _flushRequested = true;
            }
        }

        public void SetPlayerId(string playerId)
        {
            // Route through the property so the backing field stays in sync with the live
            // session; otherwise a later Shutdown()+Initialize() would rebuild the session
            // from the stale _playerId and silently revert to anonymous. Safe before init
            // (the value is applied when the session is created).
            PlayerId = playerId;
        }

        /// <summary>
        /// Begin an automated profiling session: tag every subsequent event with CI
        /// metadata so build-over-build performance can be compared in the dashboard and
        /// via <c>framedash perf-diff</c>. <paramref name="buildId"/> is stamped as the
        /// first-class build_id field; <paramref name="branch"/>, <paramref name="commit"/>
        /// and <paramref name="scenario"/> are attached as the <c>ci.branch</c> /
        /// <c>ci.commit</c> / <c>ci.scenario</c> attributes. Each call fully (re)defines the
        /// session rather than patching it: an omitted (null/empty) buildId clears any prior
        /// build_id override (events fall back to the configured BuildId) and an omitted
        /// branch/commit/scenario is absent from the new tag set -- callers cannot
        /// incrementally update metadata across calls. With all arguments empty this is a
        /// no-op. Call once after Initialize(), before the profiling run. No-op if the SDK is
        /// not initialized.
        /// </summary>
        public void BeginAutomatedSession(string buildId = null, string branch = null,
            string commit = null, string scenario = null)
        {
            try
            {
                if (!_initialized)
                {
                    GD.PushWarning("[Framedash] SDK not initialized. Call Initialize() before BeginAutomatedSession().");
                    return;
                }
                bool hasBuildId = !string.IsNullOrEmpty(buildId);
                bool hasBranch = !string.IsNullOrEmpty(branch);
                bool hasCommit = !string.IsNullOrEmpty(commit);
                bool hasScenario = !string.IsNullOrEmpty(scenario);
                // No metadata at all (e.g. BeginAutomatedSessionFromEnvironment with the
                // FRAMEDASH_* vars unset) is a true no-op: do not start an override or touch
                // session attributes, so a later End cannot clear state this call never set.
                if (!hasBuildId && !hasBranch && !hasCommit && !hasScenario) return;
                var attrs = new Dictionary<string, string>();
                if (hasBranch) attrs["ci.branch"] = branch;
                if (hasCommit) attrs["ci.commit"] = commit;
                if (hasScenario) attrs["ci.scenario"] = scenario;
                // Install the build_id override + ci.* attributes as one atomic snapshot. Each
                // Begin fully (re)defines the session: a supplied buildId becomes the override,
                // otherwise it is cleared back to the configured BuildId fallback -- the same
                // replace-don't-merge semantics as the attributes, so no stale build_id leaks
                // from a prior session.
                _session.SetAutomatedSession(hasBuildId ? buildId : null, attrs);
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] BeginAutomatedSession() failed: {e.Message}");
            }
        }

        /// <summary>
        /// Begin an automated profiling session from the standard Framedash CI environment
        /// variables: <c>FRAMEDASH_BUILD_ID</c>, <c>FRAMEDASH_GIT_BRANCH</c>,
        /// <c>FRAMEDASH_GIT_COMMIT</c>, <c>FRAMEDASH_TEST_SCENARIO</c>. The
        /// <c>framedash run-profile-test</c> runner exports these before launching the
        /// game, so a CI integration needs only this one call in its automated-test entry
        /// point. With none of the variables set this is a no-op (no override is started).
        /// No-op if the SDK is not initialized.
        /// </summary>
        public void BeginAutomatedSessionFromEnvironment()
        {
            try
            {
                // Fully-qualify System.Environment: `using Godot;` also brings a
                // Godot.Environment type into scope, so the bare name is ambiguous.
                BeginAutomatedSession(
                    System.Environment.GetEnvironmentVariable("FRAMEDASH_BUILD_ID"),
                    System.Environment.GetEnvironmentVariable("FRAMEDASH_GIT_BRANCH"),
                    System.Environment.GetEnvironmentVariable("FRAMEDASH_GIT_COMMIT"),
                    System.Environment.GetEnvironmentVariable("FRAMEDASH_TEST_SCENARIO"));
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] BeginAutomatedSessionFromEnvironment() failed: {e.Message}");
            }
        }

        /// <summary>
        /// End the automated profiling session: clear the <c>ci.*</c> session attributes set
        /// by <see cref="BeginAutomatedSession"/> AND drop the automated-session build_id
        /// override, so events emitted afterward carry the configured build_id again and are
        /// no longer folded into the candidate build's perf diff. Call <see cref="Flush"/>
        /// first if you want the buffered tagged events sent before the tags are cleared.
        /// No-op if the SDK is not initialized.
        /// </summary>
        public void EndAutomatedSession()
        {
            try
            {
                if (!_initialized) return;
                // One atomic clear: the build_id override and the ci.* attributes live in a
                // single session snapshot, so a background Track() either sees the whole
                // session or none of it -- a post-End event can never carry the candidate
                // build_id with cleared tags.
                _session.ClearSessionAttributes();
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] EndAutomatedSession() failed: {e.Message}");
            }
        }

        public void SetEventSamplingRate(string eventName, float rate)
        {
            if (!_initialized)
            {
                GD.PushWarning("[Framedash] SDK not initialized. Call Initialize() first.");
                return;
            }
            _samplingPolicy.SetEventRate(FieldClamp.TruncateEventName(eventName), rate);
        }

        /// <summary>
        /// Remove a per-event-name sampling override so the event falls back to the global rate.
        /// Returns true if an override was present.
        /// </summary>
        public bool RemoveEventSamplingRate(string eventName)
        {
            if (!_initialized) return false;
            return _samplingPolicy.RemoveEventRate(FieldClamp.TruncateEventName(eventName));
        }

    }
}
