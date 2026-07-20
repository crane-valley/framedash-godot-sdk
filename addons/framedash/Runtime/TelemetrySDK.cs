using System;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;

namespace Framedash
{
    /// <summary>
    /// Main entry point for the Framedash Telemetry SDK.
    /// Register as an autoload singleton (Project Settings -> Autoload) or use
    /// <see cref="Initialize"/>. Node-derived, so it MUST be partial (the Godot
    /// C# source generators require partial for any Node subclass).
    /// </summary>
    public partial class TelemetrySDK : Node
    {
        // volatile: published to background-thread readers via the double-checked-locked
        // Instance getter; prevents seeing a non-null but not-yet-published reference.
        private static volatile TelemetrySDK s_instance;
        // Guards the auto-create path in the Instance getter so concurrent access from
        // multiple threads cannot create duplicate nodes (double-checked locking).
        private static readonly object s_instanceLock = new object();
        // True only for an instance the Instance getter auto-created as a fallback (no
        // autoload registered). A real autoload / inspector-configured node always wins
        // over an auto-created one in _EnterTree, so inspector config is never discarded.
        private bool _autoCreated;
        private const int DefaultMaxBatchSize = 100;
        // Matches the consumer's MAX_EVENTS_PER_BATCH (packages/ingest-core/src/config.ts):
        // a batch larger than the server cap is rejected wholesale, so allowing the
        // Inspector to configure one only loses data. 10,000 also equals the default
        // EventBuffer capacity; real flushes stay in the low hundreds (~100KB payload trigger).
        private const int MaxInspectorBatchSize = 10000;
        private const int MaxInspectorEventBufferCapacity = MaxInspectorBatchSize * 2;
        // Per-event field clamping (event name, map_id, build_id, position, platform,
        // engine_version, attributes/metrics caps) is centralized in FieldClamp
        // (surrogate-pair safe; shared with the Unity SDK). Over-limit/out-of-range
        // fields are rejected by ingest validation, which drops the whole batch, so
        // the SDK clamps them client-side before buffering.

        // Configuration is exposed via [Export] so it shows in the Godot inspector
        // when the script is attached to a node or registered as an autoload. Same
        // defaults as the Unity SDK.
        [Export] public string EndpointUrl { get; set; } = "https://ingest.framedash.dev/v1/events";
        [Export] public string ApiKey { get; set; } = "";
        [Export] public string BuildId { get; set; } = "";
        // Code constant, deliberately NOT [Export]ed: an exported version property
        // gets captured into saved scenes/autoload state and would deserialize the
        // OLD value over this initializer after an addon upgrade, leaving the
        // X-SDK-Version header stale. Keep in sync with plugin.cfg (release gotcha).
        public const string SdkVersion = "0.1.7";
        private string _playerId = "";
        [Export]
        public string PlayerId
        {
            get => _playerId;
            set
            {
                _playerId = value;
                // Propagate a runtime change to the live session so events pick up the new
                // player_id immediately (no-op before init; SessionManager trims/normalizes).
                _session?.SetPlayerId(value);
            }
        }

        [Export]
        public bool CaptureCameraRotation
        {
            get => _captureCameraRotation;
            set
            {
                _captureCameraRotation = value;
                // Drop any cached sample so a toggle never stamps a stale reading;
                // the next _Process() repopulates it while enabled.
                Interlocked.Exchange(ref _cameraSnapshot, CameraMath.CameraAbsent);
            }
        }

        // Batching configuration. Range hints mirror Unity's [Range] so the inspector
        // shows the same bounds; the values are still re-validated/clamped at init.
        // PropertyHint.Range requires a compile-time constant string, so the upper
        // bounds are spelled literally here and kept in sync with the int consts
        // above (MaxInspectorBatchSize=10000, MaxInspectorEventBufferCapacity=20000).
        [Export(PropertyHint.Range, "1,10000")]
        public int MaxBatchSize { get; set; } = DefaultMaxBatchSize;
        [Export(PropertyHint.Range, "1,20000")]
        public int EventBufferCapacity { get; set; } = EventBuffer.DefaultCapacity;
        [Export] public float FlushIntervalSeconds { get; set; } = 30f;
        [Export] public int MaxPayloadBytes { get; set; } = 102400; // 100KB
        // Transport resilience (F49). A wedged endpoint (e.g. an IPv6 blackhole with no
        // Happy Eyeballs) blocks each attempt for the full HTTP timeout while holding the
        // single-flight flush guard, so a short per-attempt timeout + small retry budget
        // bound the worst-case flush wall-time (~33s at the defaults) and keep later
        // flushes from being starved. Both are re-validated to a positive value at init.
        [Export] public int HttpTimeoutSeconds { get; set; } = 10;
        [Export] public int MaxRetries { get; set; } = RetryPolicy.DefaultMaxRetries;
        // Opt-in positive delivery confirmation (F25): logs "Flushed N events (HTTP 202)"
        // on each successful batch. Off by default so it never spams a shipping game;
        // first-time integrators flip it on to confirm delivery client-side.
        private bool _verboseLogging;
        [Export]
        public bool VerboseLogging
        {
            get => _verboseLogging;
            set
            {
                _verboseLogging = value;
                // Propagate a runtime toggle to the live transport so flipping this in
                // the inspector (or via code) takes effect immediately instead of being
                // frozen at the value captured when the session was initialized. No-op
                // before init (applied when the transport is created).
                if (_transport != null) _transport.VerboseLogging = value;
            }
        }

        private float _samplingRate = 1f;
        [Export(PropertyHint.Range, "0,1")]
        public float SamplingRate
        {
            get => _samplingRate;
            set
            {
                _samplingRate = value;
                // Propagate a runtime change to the live sampling policy (clamped to [0,1]
                // there; no-op before init).
                if (_samplingPolicy != null) _samplingPolicy.Rate = value;
            }
        }

        private bool _captureCameraRotation = true;

        private EventBuffer _buffer;
        private TransportLayer _transport;
        private SessionManager _session;
        private PerformanceCollector _perfCollector;
        private SamplingPolicy _samplingPolicy;
        private FlushPolicy _flushPolicy;
        // volatile: written on the main thread (InitializeInternal/Shutdown), read from
        // background threads in Track(); ensures the initialized state and the fields it
        // guards (_buffer/_session/etc.) are visible across threads without caching.
        private volatile bool _initialized;
        // True once Shutdown() has run on an explicitly-initialized SDK. Prevents _Ready's
        // auto-init from REVIVING a shut-down instance. Critical for an auto-created node
        // (no autoload) whose DEFERRED tree entry races a same-frame Initialize + Shutdown():
        // without this, when the node finally enters the tree _Ready would see _initialized
        // == false + a non-empty (Initialize-set) ApiKey and re-initialize, disposing the
        // transport out from under the parked best-effort final flush (it would then send on a
        // freed HttpRequest). Cleared on a genuine re-init. Written/read on the main thread only.
        private bool _shutdownCalled;
        private int _estimatedPayloadBytes;
        // Single-flight flush guard + re-init generation, extracted to FlushGate so the
        // concurrency semantics are unit-tested (engine-free). Only one flush is in
        // flight at a time (the transport owns a single HttpRequest node); a stale flush
        // from a prior session cannot clear the guard the new session now owns (#1044).
        private readonly FlushGate _flushGate = new FlushGate();
        // Wall-clock cap for the synchronous shutdown drain (DrainBlocking). Small on
        // purpose: a terminal-quit blocking POST must never make the app hang closing, so
        // a wedged endpoint is abandoned after this budget and the events are lost
        // (best-effort, no offline queue). Comfortably covers a healthy resolve + TLS +
        // POST round trip; the async runtime path keeps its own larger retry budget.
        private const int ShutdownDrainBudgetMs = 2500;
        // Retains the batch handed to the current in-flight async FlushAsync. A normal
        // flush dequeues its events into a fire-and-forget task; if quit races that send
        // (buffer already empty, task not yet resumed), synchronous teardown would lose
        // it. Keeping the reference lets the shutdown drain resend it via the blocking
        // path. Cleared when that flush resolves. Main-thread only: Flush() marshals to
        // the main thread and the FlushAsync continuation resumes there too.
        private TelemetryEvent[] _inFlightBatch;
        private volatile bool _flushRequested;
        private bool _warnedEmptyPlayerId;
        private string _cachedPlatform;
        private string _cachedEngineVersion;
        // mem.* metrics list (mem.vram / mem.textures / mem.buffers), rebuilt ONLY at
        // heartbeat cadence (see TrackInternal) and shared by reference with every
        // position-qualified event tracked before the next heartbeat -- rebuilding per
        // event would allocate a new List<FloatPair> on the spatial telemetry hot path,
        // violating the allocation-discipline hard rule. volatile so a Track() call on a
        // background thread observes the latest reference published from the main
        // thread's heartbeat without a lock. Null until the first heartbeat runs, and
        // whenever the current sample has nothing to report (all monitors <= 0).
        // Refresh REPLACES this field with a brand-new list; it is never mutated in
        // place, because a TelemetryEvent already holding the old reference can still be
        // sitting unflushed in the EventBuffer ring buffer (see MemMetricsBuilder.AttachTo).
        private volatile List<FloatPair> _cachedMemMetrics;
        // The automated-session (CI) build_id override and ci.* attributes live together in
        // the SessionManager as one immutable snapshot (see SessionManager.ResolveSessionStamp),
        // so the public [Export] BuildId property is never overwritten and the stamping path
        // reads the build_id and the tags from a single consistent point.
        // Camera yaw/pitch sampled once per frame (_Process) and stamped onto events,
        // mirroring the per-frame performance cache. Packed into one long and
        // published/read atomically so the (yaw, pitch) pair is always observed
        // coherently. CameraAbsent means "no camera this frame".
        private long _cameraSnapshot = CameraMath.CameraAbsent;
        private const float HeartbeatIntervalSeconds = 10f;
        private float _timeSinceLastHeartbeat;
        // Manual-feed disk I/O accumulator (Godot exposes no engine I/O counters --
        // see PLANS.md "Storage/disk I/O metrics capture"). Recreated on every
        // (re-)init so a new session never inherits a stale window or "ever active"
        // flag from a prior session.
        private IoStats _ioStats;
        // Map/level load-time helper (BeginMapLoad/EndMapLoad/ReportMapLoad). Holds
        // the pending measurement and computes elapsed ms; the load time rides the
        // metrics map as load_time_ms on a "map_load" event (no proto/CH change,
        // mirroring the io.* attributes-map guardrail). Recreated on each (re-)init
        // so a new session never completes a load begun by a prior one.
        private MapLoadTimer _mapLoadTimer;
        // Real-time clocks for the heartbeat and flush loop. Godot has no coroutines,
        // so the Unity FlushLoop is folded into _Process(). The _Process(double delta)
        // param is engine-scaled (it follows Engine.TimeScale and pauses with the
        // tree), exactly like Unity's Time.deltaTime — so for the heartbeat and flush
        // timers we measure REAL elapsed time via Time.GetTicksUsec() instead, the
        // analogue of Unity's Time.unscaledDeltaTime / realtimeSinceStartup.
        private ulong _lastProcessTicksUsec;
        private ulong _lastFlushTicksUsec;

        /// <summary>Current session ID, or null if SDK is not initialized.</summary>
        public string SessionId
        {
            get
            {
                if (!_initialized || _session == null)
                {
                    GD.PushWarning("[Framedash] SDK is not initialized. Call Initialize() first.");
                    return null;
                }
                return _session.SessionId;
            }
        }

        /// <summary>Whether the SDK is initialized and ready to track events.</summary>
        public bool IsInitialized => _initialized;

        /// <summary>
        /// Singleton instance. Auto-created and added to the scene tree root if needed,
        /// mirroring the Unity SDK's auto-create. Returns null only if there is no
        /// running SceneTree (e.g. called from a tool/editor context with no main loop).
        /// </summary>
        public static TelemetrySDK Instance
        {
            get
            {
                if (s_instance != null) return s_instance;
                lock (s_instanceLock)
                {
                    // Double-checked: another thread may have created it while we waited.
                    if (s_instance != null) return s_instance;

                    // Creating/parenting a Node off the main thread is unsafe in Godot. The
                    // auto-create fallback is only for main-thread first access; if reached
                    // from a background thread before the SDK exists, refuse rather than risk
                    // a crash -- initialize on the main thread (or enable the plugin autoload).
                    if (OS.GetThreadCallerId() != OS.GetMainThreadId())
                    {
                        GD.PushError("[Framedash] TelemetrySDK.Instance was accessed from a background thread before initialization. Initialize the SDK on the main thread (or enable the plugin autoload) first.");
                        return null;
                    }

                    var tree = Engine.GetMainLoop() as SceneTree;
                    if (tree == null)
                    {
                        GD.PushWarning("[Framedash] No SceneTree available; cannot auto-create the SDK node.");
                        return null;
                    }

                    // Auto-create a node and parent it to the tree root. AddChild is
                    // deferred so this is safe even when called mid-frame (e.g. during
                    // another node's _Ready/_Process). _EnterTree sets s_instance once the
                    // node is actually in the tree, but assign here too so callers in the
                    // same frame (before the deferred add runs) get a usable reference.
                    var node = new TelemetrySDK { Name = "Framedash" };
                    node._autoCreated = true;
                    tree.Root.CallDeferred(Node.MethodName.AddChild, node);
                    s_instance = node;
                    return s_instance;
                }
            }
        }

        /// <summary>
        /// Initialize the SDK with the given configuration.
        /// Call this once at game startup (e.g., in a boot scene/autoload).
        /// </summary>
        public static TelemetrySDK Initialize(string apiKey, string endpointUrl = null, string buildId = null, string playerId = null)
        {
            var sdk = Instance;
            if (sdk == null) return null;
            if (sdk.IsInitialized)
            {
                // Reject BEFORE mutating config so a duplicate Initialize() cannot leave the
                // live session sending with the old key/endpoint but new BuildId/PlayerId.
                GD.PushWarning("[Framedash] SDK is already initialized; Initialize() ignored. Call Shutdown() first to reconfigure.");
                return sdk;
            }
            sdk.ApiKey = apiKey;
            if (!string.IsNullOrEmpty(endpointUrl)) sdk.EndpointUrl = endpointUrl;
            if (!string.IsNullOrEmpty(buildId)) sdk.BuildId = buildId;
            if (playerId != null) sdk.PlayerId = playerId;
            sdk.InitializeInternal();
            return sdk;
        }

        public override void _EnterTree()
        {
            // Enforce the singleton. A registered autoload (or inspector-configured node)
            // must win over an instance the Instance getter auto-created as a fallback:
            // if early code touches Instance before the autoload enters the tree, the
            // empty auto-created node would otherwise survive while the real autoload
            // frees itself here, silently discarding the user's inspector configuration.
            if (s_instance != null && s_instance != this)
            {
                if (s_instance._autoCreated && !_autoCreated && !s_instance._initialized)
                {
                    // This is the real autoload and the auto-created fallback has not been
                    // initialized yet -- replace it so inspector config is used. If the
                    // fallback was already initialized (events buffered), fall through and
                    // let this autoload free itself instead, preserving the running instance.
                    s_instance.QueueFree();
                    s_instance = this;
                    return;
                }
                // Genuine duplicate -- remove it. Defer free so we never free a node
                // mid tree-signal dispatch.
                QueueFree();
                return;
            }
            s_instance = this;
        }

        public override void _Ready()
        {
            // Keep _Process (heartbeat + flush cadence) running even when the SceneTree is
            // paused, so a pause menu does not silently stop telemetry flushing.
            ProcessMode = ProcessModeEnum.Always;
            // Auto-initialize when an API key was supplied via the inspector/autoload,
            // mirroring Unity's Start(). Code-driven setups call Initialize() instead.
            // Skip after an explicit Shutdown() (_shutdownCalled): a same-frame
            // Initialize+Shutdown on an auto-created node would otherwise re-init here when
            // the deferred tree entry finally fires, reviving the SDK and disposing the
            // transport under the parked final flush.
            if (!_initialized && !_shutdownCalled && !string.IsNullOrEmpty(ApiKey))
            {
                InitializeInternal();
            }
        }

        public override void _Process(double delta)
        {
            if (!_initialized) return;

            // The entire per-frame body is wrapped so a telemetry hiccup can never throw
            // out of Godot's engine callback and crash the game (the NEVER-crash hard
            // rule), mirroring the wrapping on _Notification.
            try
            {
                // Resolve the viewport once. GetViewport() returns null when this node is
                // not currently inside the tree (transient teardown / tool contexts), so
                // skip the viewport-dependent metrics rather than dereferencing null.
                Viewport viewport = GetViewport();
                if (viewport != null)
                {
                    // The viewport RID is what Godot's RenderingServer keys its frame
                    // profiler by (GPU / render-CPU timings).
                    _perfCollector.UpdateFrameTimings(viewport.GetViewportRid());
                    if (_captureCameraRotation) UpdateCameraRotation(viewport);
                }

                // Advance the real-time clocks from the monotonic usec timer rather than
                // the scaled 'delta' so pausing the tree / changing Engine.TimeScale does
                // not stall the heartbeat or flush cadence (matches Unity's unscaled timing).
                ulong nowUsec = Time.GetTicksUsec();
                if (_lastProcessTicksUsec == 0UL) _lastProcessTicksUsec = nowUsec;
                float realDelta = (nowUsec - _lastProcessTicksUsec) / 1_000_000f;
                _lastProcessTicksUsec = nowUsec;

                _timeSinceLastHeartbeat += realDelta;
                if (_timeSinceLastHeartbeat >= HeartbeatIntervalSeconds)
                {
                    _timeSinceLastHeartbeat = 0f;
                    TrackAutomated("perf_heartbeat");
                }

                // Flush-loop timing folded in from Unity's FlushLoop: trigger a flush when
                // the interval has elapsed since the last flush, or when Track() has set
                // _flushRequested. Per-frame bool/subtract checks are negligible; the
                // battery/network cost comes from the actual I/O in Flush().
                float elapsedSinceFlush = (nowUsec - _lastFlushTicksUsec) / 1_000_000f;
                if (_flushPolicy.ShouldFlush(_flushRequested, elapsedSinceFlush))
                {
                    _lastFlushTicksUsec = nowUsec;
                    Flush();
                }
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] _Process failed: {e.Message}");
            }
        }

        // Sample the active 3D camera once per frame (_Process) so each event is stamped
        // with the latest orientation without re-reading Godot APIs per event.
        private void UpdateCameraRotation(Viewport viewport)
        {
            // Resolve the current viewport's active Camera3D each frame so a switched
            // camera is always reflected (a cached reference would pin a stale camera).
            // GetCamera3D() is null on headless/2D-only viewports.
            var cam = viewport.GetCamera3D();
            if (cam == null)
            {
                Interlocked.Exchange(ref _cameraSnapshot, CameraMath.CameraAbsent);
                return;
            }

            // Godot cameras look down -Z in their local basis; the world-space forward
            // is therefore -(global basis Z). CameraMath derives wire-convention
            // (yaw, pitch) from that forward vector.
            Vector3 forward = -cam.GlobalTransform.Basis.Z;
            CameraMath.YawPitchFromForward(forward.X, forward.Y, forward.Z, out float yaw, out float pitch);

            // Finite-only: publish the coherent pair, or the absent sentinel.
            if (float.IsNaN(yaw) || float.IsInfinity(yaw) ||
                float.IsNaN(pitch) || float.IsInfinity(pitch))
            {
                Interlocked.Exchange(ref _cameraSnapshot, CameraMath.CameraAbsent);
                return;
            }

            Interlocked.Exchange(ref _cameraSnapshot, CameraMath.PackCamera(yaw, pitch));
        }

        private void InitializeInternal()
        {
            if (_initialized)
            {
                GD.PushWarning("[Framedash] SDK is already initialized; re-initialization ignored. Call Shutdown() first to reconfigure.");
                return;
            }
            // Initialization creates scene-tree nodes (the transport's HttpRequest child),
            // which is main-thread-only in Godot. Refuse to initialize off the main thread
            // (covers Initialize() reached on a background thread when the autoload already
            // exists, where the Instance getter returns without a thread check).
            if (OS.GetThreadCallerId() != OS.GetMainThreadId())
            {
                GD.PushError("[Framedash] Initialize must be called on the main thread (it creates scene-tree nodes).");
                return;
            }
            if (string.IsNullOrEmpty(ApiKey))
            {
                GD.PushError("[Framedash] API key is required. Call TelemetrySDK.Initialize(apiKey).");
                return;
            }

            // Validate endpoint URL
            if (!Uri.TryCreate(EndpointUrl, UriKind.Absolute, out var parsedUri) ||
                (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
            {
                GD.PushError("[Framedash] endpointUrl must be a valid HTTP(S) URL.");
                return;
            }

            int maxBatchSize = ResolveMaxBatchSize();
            int maxPayloadBytes = ResolveMaxPayloadBytes();
            _buffer = new EventBuffer(ResolveEventBufferCapacity(maxBatchSize));
            // Dispose any prior transport before creating a new one so re-initialization
            // (Shutdown then Initialize) frees the old HttpRequest child node instead of
            // leaking it under this autoload.
            _transport?.Dispose();
            // Re-init starts fresh: clear flush state left over from a prior session so a
            // previous in-flight flush (now using a disposed transport) cannot block the new
            // session's flushes -- including session_start -- via the single-flight guard.
            // Reset() clears the guard AND bumps the generation, so a stale FlushAsync
            // completion from a prior session will not release this session's guard.
            _flushRequested = false;
            _flushGate.Reset();
            // A genuine (re-)init clears the shutdown latch so a subsequent Shutdown() can set
            // it again and _Ready's auto-init guard reflects the current lifecycle state.
            _shutdownCalled = false;
            Interlocked.Exchange(ref _estimatedPayloadBytes, 0);
            // A fresh session owns no automated-session state: the SessionManager (which holds
            // the build_id override + ci.* snapshot) is recreated below, so a prior Begin
            // without an End cannot keep stamping events under the candidate build.
            // Pass 'this' as the owning Node so the transport can issue HTTP requests
            // through Godot's HTTPRequest node lifecycle.
            _transport = new TransportLayer(this, EndpointUrl, ApiKey, SdkVersion, maxPayloadBytes,
                ResolveHttpTimeoutSeconds(), ResolveMaxRetries(), VerboseLogging);
            _session = new SessionManager(PlayerId);
            _perfCollector = new PerformanceCollector();
            _ioStats = new IoStats();
            _mapLoadTimer = new MapLoadTimer();
            // Reset the camera snapshot so a re-init (Shutdown then Initialize) does
            // not stamp session_start / pre-first-_Process events with a stale reading.
            Interlocked.Exchange(ref _cameraSnapshot, CameraMath.CameraAbsent);
            _samplingPolicy = new SamplingPolicy(SamplingRate);
            _flushPolicy = new FlushPolicy(maxBatchSize, maxPayloadBytes, FlushIntervalSeconds);
            // platform / engine_version are per-event fields capped at 64 by ingest
            // (engine_version can carry a long custom build string), so clamp the
            // cached values once here rather than per event.
            _cachedPlatform = FieldClamp.Truncate(OS.GetName() ?? "", FieldClamp.MaxPlatformLength);
            _cachedEngineVersion = FieldClamp.Truncate(ReadEngineVersion() ?? "", FieldClamp.MaxEngineVersionLength);

            _timeSinceLastHeartbeat = 0f;
            // Seed the real-time clocks so the first _Process tick measures a real delta
            // and the flush interval is counted from init, not from epoch 0.
            ulong nowUsec = Time.GetTicksUsec();
            _lastProcessTicksUsec = nowUsec;
            _lastFlushTicksUsec = nowUsec;
            _initialized = true;

            GD.Print($"[Framedash] SDK initialized. Session: {_session.SessionId}");
            TrackAutomated("session_start");
        }

        // Engine.GetVersionInfo() returns a Dictionary; read the "string" key
        // defensively so a malformed/absent entry cannot throw out of Initialize()
        // (which may be invoked from _Ready, an engine callback). Falls back to "".
        private static string ReadEngineVersion()
        {
            try
            {
                var info = Engine.GetVersionInfo();
                if (info != null && info.TryGetValue("string", out Variant v))
                    return v.AsString();
            }
            catch
            {
                // Fall through to empty on any native/cast hiccup.
            }
            return "";
        }

        // Clamp the configured payload limit to a positive value (default 100KB). A
        // non-positive MaxPayloadBytes would make TransportLayer split every multi-event
        // batch down to single-event requests; resolve once and pass the same value to
        // both the transport and the flush policy.
        private int ResolveMaxPayloadBytes()
        {
            int maxPayloadBytes = MaxPayloadBytes;
            if (maxPayloadBytes <= 0)
            {
                maxPayloadBytes = 102400;
                GD.PushWarning($"[Framedash] Max payload bytes must be > 0. Using default {maxPayloadBytes}.");
            }
            return maxPayloadBytes;
        }

        // Upper bounds for the transport-resilience exports. A misconfigured Inspector
        // value must not be able to recreate the very starvation this feature fixes: a
        // huge timeout or retry budget would let a wedged endpoint hold the single-flight
        // guard for minutes again (and a large retry count would also overflow the
        // exponential backoff). 60s x 10 attempts is already well beyond any sane setting.
        private const int MaxHttpTimeoutSeconds = 60;
        private const int MaxAllowedRetries = 10;

        // Clamp the configured per-attempt HTTP timeout into (0, MaxHttpTimeoutSeconds].
        // A non-positive value falls back to the RetryPolicy default (10s); an oversized
        // value is clamped so a misconfiguration cannot recreate unbounded starvation.
        private int ResolveHttpTimeoutSeconds()
        {
            int seconds = HttpTimeoutSeconds;
            if (seconds <= 0)
            {
                seconds = (int)RetryPolicy.DefaultHttpTimeoutSeconds;
                GD.PushWarning($"[Framedash] HTTP timeout must be > 0. Using default {seconds}s.");
            }
            else if (seconds > MaxHttpTimeoutSeconds)
            {
                GD.PushWarning($"[Framedash] HTTP timeout ({seconds}s) exceeds the supported maximum ({MaxHttpTimeoutSeconds}s). Clamping.");
                seconds = MaxHttpTimeoutSeconds;
            }
            return seconds;
        }

        // Clamp the configured retry budget into (0, MaxAllowedRetries]. A non-positive
        // value falls back to the RetryPolicy default (3 attempts); an oversized value is
        // clamped so a misconfiguration cannot recreate unbounded starvation or overflow
        // the exponential backoff.
        private int ResolveMaxRetries()
        {
            int retries = MaxRetries;
            if (retries <= 0)
            {
                retries = RetryPolicy.DefaultMaxRetries;
                GD.PushWarning($"[Framedash] Max retries must be > 0. Using default {retries}.");
            }
            else if (retries > MaxAllowedRetries)
            {
                GD.PushWarning($"[Framedash] Max retries ({retries}) exceeds the supported maximum ({MaxAllowedRetries}). Clamping.");
                retries = MaxAllowedRetries;
            }
            return retries;
        }

        private int ResolveMaxBatchSize()
        {
            int maxBatchSize = MaxBatchSize;
            if (maxBatchSize <= 0)
            {
                maxBatchSize = DefaultMaxBatchSize;
                GD.PushWarning($"[Framedash] Max batch size must be > 0. Using default {maxBatchSize}.");
            }

            if (maxBatchSize > MaxInspectorBatchSize)
            {
                GD.PushWarning(
                    $"[Framedash] Max batch size ({maxBatchSize}) exceeds the supported maximum " +
                    $"({MaxInspectorBatchSize}). Clamping to supported maximum.");
                maxBatchSize = MaxInspectorBatchSize;
            }

            return maxBatchSize;
        }

        private int ResolveEventBufferCapacity(int maxBatchSize)
        {
            int capacity = EventBufferCapacity;
            if (capacity <= 0)
            {
                capacity = EventBuffer.DefaultCapacity;
                GD.PushWarning($"[Framedash] Event buffer capacity must be > 0. Using default {capacity}.");
            }

            if (capacity > MaxInspectorEventBufferCapacity)
            {
                GD.PushWarning(
                    $"[Framedash] Event buffer capacity ({capacity}) exceeds the supported maximum " +
                    $"({MaxInspectorEventBufferCapacity}). Clamping to supported maximum.");
                capacity = MaxInspectorEventBufferCapacity;
            }

            int safetyMargin = maxBatchSize * 2;

            if (capacity < safetyMargin)
            {
                GD.PushWarning(
                    $"[Framedash] Event buffer capacity ({capacity}) is smaller than recommended safety margin " +
                    $"({safetyMargin}). Clamping to safety margin.");
                capacity = safetyMargin;
            }

            return capacity;
        }

        /// <summary>
        /// Track a custom event.
        /// </summary>
        /// <param name="eventName">Name of the event (e.g. "player_death", "zone_enter"). Must not be null or empty.</param>
        /// <param name="mapId">Optional map identifier for spatial context.</param>
        /// <param name="position">Optional world-space position where the event occurred.</param>
        /// <param name="attributes">Optional string key-value pairs for categorical data.</param>
        /// <param name="metrics">Optional float key-value pairs for numerical measurements.</param>
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
        /// completes to emit a <c>map_load</c> event whose <c>map_id</c> is the map name
        /// and whose <c>load_time_ms</c> metric is the elapsed time. The clock is
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

        /// <summary>
        /// Complete the map/level load started by <see cref="BeginMapLoad"/> and emit a
        /// <c>map_load</c> event (map_id = the stored map name, metrics
        /// <c>load_time_ms</c> = elapsed milliseconds) via the normal Track path
        /// (sampling, buffering, session attributes). No-op if no BeginMapLoad is pending
        /// or the SDK is not initialized. Safe to call from any thread. Never throws.
        /// </summary>
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

        /// <summary>
        /// Directly report a map/level load time for developers who measure it themselves
        /// (custom loaders / streaming), bypassing the Begin/End timer. Emits the same
        /// <c>map_load</c> event shape (map_id = <paramref name="mapName"/>, metrics
        /// <c>load_time_ms</c> = <paramref name="loadTimeMs"/>). A NaN, Infinity, or
        /// negative <paramref name="loadTimeMs"/> is DROPPED (the whole call, not clamped),
        /// matching the manual metric-feed contract; the map name is clamped to the ingest
        /// map_id cap. Safe to call from any thread. Never throws. No-op if the SDK is not
        /// initialized.
        /// </summary>
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

        // Shared event-construction and enqueue/flush-check logic.
        // All caller-specific gates (initialization check, name validation,
        // sampling, player-ID warning, attribute conversion) run in the caller
        // before this method is invoked with fully resolved values.
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

            // Read the camera snapshot atomically; TryUnpackCamera yields a coherent
            // pair or nothing (both-or-neither). The serializer is the final guard.
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

            var evt = new TelemetryEvent
            {
                EventName = eventName,
                // Unix epoch in .NET ticks (621355968000000000L), divided by 10
                // to convert 100ns ticks to microseconds for true microsecond precision.
                TimestampUs = Math.Max(0L, (DateTimeOffset.UtcNow.Ticks - 621355968000000000L) / 10L),
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

        /// <summary>
        /// Set the player ID at runtime (e.g. after login).
        /// Pass null or empty to revert to anonymous.
        /// </summary>
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
        /// <c>FRAMEDASH_GIT_COMMIT</c>, <c>FRAMEDASH_TEST_SCENARIO</c>. The planned
        /// <c>framedash run-profile-test</c> runner will export these before launching the
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

        /// <summary>
        /// Set a per-event-name sampling rate that overrides the global rate for that event.
        /// Empty event names are ignored. Rate is clamped to [0, 1].
        /// Has no effect if the SDK is not initialized.
        /// </summary>
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

        /// <summary>Flush all buffered events immediately. Safe to call from any thread (a background-thread call is marshalled to the main thread).</summary>
        public void Flush()
        {
            // Flush touches the scene tree / HttpRequest, which are main-thread only. If
            // called from a background thread (Track-from-thread is supported), marshal the
            // call to the main thread instead of risking an off-main engine call.
            if (OS.GetThreadCallerId() != OS.GetMainThreadId())
            {
                Callable.From(Flush).CallDeferred();
                return;
            }
            try
            {
                if (!_flushGate.TryBegin()) return;
                if (!_initialized || _buffer.Count == 0)
                {
                    // Nothing to flush -- clear any pending request so _Process does not
                    // re-invoke Flush() every frame while the buffer stays empty.
                    _flushRequested = false;
                    _flushGate.Release();
                    return;
                }
                // Reset _flushRequested AFTER the guard so a background-thread request
                // arriving between the two checks is not silently dropped.
                _flushRequested = false;
                Interlocked.Exchange(ref _estimatedPayloadBytes, 0);
                // Godot uses async/await rather than coroutines: launch the send
                // fire-and-forget. FlushAsync awaits the transport in a try/finally
                // that always releases the gate so a failed send cannot wedge the
                // single-flight guard. The bounded transport wall-time keeps that
                // window small so later flushes are not starved. The discard documents
                // the intentional non-await. Retain the batch so a shutdown that races
                // this unconfirmed send can resend it via the blocking drain.
                var batch = _buffer.DequeueAll();
                _inFlightBatch = batch;
                _ = FlushAsync(batch, _flushGate.Generation);
            }
            catch (Exception e)
            {
                _flushGate.Release();
                GD.PushError($"[Framedash] Flush() failed: {e.Message}");
            }
        }

        // Awaits the transport send and always releases the single-flight guard.
        // Async void would let an exception escape to the synchronization context;
        // returning Task with a try/catch keeps the SDK fail-safe even though the
        // caller discards the task.
        private async Task FlushAsync(TelemetryEvent[] events, int generation)
        {
            try
            {
                await _transport.SendBatch(events);
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] Flush send failed: {e.Message}");
            }
            finally
            {
                // Drop the in-flight retention once this flush has resolved (success or
                // failure): a later shutdown has nothing unconfirmed to resend. Reference-
                // compare so a newer flush's batch is never cleared here.
                if (ReferenceEquals(_inFlightBatch, events)) _inFlightBatch = null;
                // Only release the guard if no re-init happened meanwhile; otherwise the new
                // generation owns the gate and clearing it here could let two sends run.
                _flushGate.ReleaseIfGeneration(generation);
            }
        }

        // Deliver the buffered events SYNCHRONOUSLY on a terminal-quit path. The async
        // Flush() cannot complete once the app is quitting -- the main loop stops before
        // the awaited SendBatch continuation runs, and the owner node leaves the tree --
        // so a fire-and-forget send would only dequeue the buffer into a doomed task and
        // lose the final events (typically the last perf_heartbeat). The transport's
        // blocking HttpClient path needs no scene-tree node and is bounded so quit is
        // never hung. Main-thread only: every caller is an engine teardown callback or the
        // already-marshaled Shutdown.
        private void DrainBlocking()
        {
            // Called from Shutdown AFTER _initialized was flipped false (so no new events
            // are accepted); do not re-check _initialized here or the drain would no-op.
            if (_transport == null) return;

            // Recover a batch an async flush already dequeued but has not confirmed: its
            // awaited send cannot resume during synchronous teardown, so those events
            // (already gone from the buffer) would otherwise be lost.
            var inFlight = _inFlightBatch;
            _inFlightBatch = null;

            TelemetryEvent[] buffered = _buffer != null && _buffer.Count > 0
                ? _buffer.DequeueAll()
                : System.Array.Empty<TelemetryEvent>();

            // Send the buffered events and the in-flight batch as SEPARATE envelopes, never
            // concatenated: the consumer dedups by hashing the full ordered event array, so
            // re-sending the in-flight batch with its ORIGINAL array is dropped if it
            // already reached ingest, whereas merging it with the buffered events would
            // hash differently and duplicate every in-flight event (see
            // BatchPolicy.BuildShutdownEnvelopes). Buffered goes first (guaranteed-
            // undelivered final events). Both share one drain budget.
            TelemetryEvent[][] envelopes = BatchPolicy.BuildShutdownEnvelopes(buffered, inFlight);
            if (envelopes.Length == 0) return;

            // Clear the running payload estimate so a later no-op flush cannot act on a
            // stale byte count.
            Interlocked.Exchange(ref _estimatedPayloadBytes, 0);

            _transport.SendEnvelopesBlocking(envelopes, ShutdownDrainBudgetMs);
        }

        /// <summary>Shutdown the SDK gracefully (final best-effort flush). Safe to call from any thread (a background-thread call is marshalled to the main thread).</summary>
        public void Shutdown()
        {
            // Shutdown mutates lifecycle state (_initialized/_shutdownCalled) and drives the
            // final Flush(), all of which touch the scene tree / must stay ordered on the main
            // thread. Marshal the WHOLE method to the main thread (like Flush) when called off
            // it: otherwise Flush() would defer itself and return, and _initialized=false would
            // then be set on the background thread BEFORE the deferred flush runs, so the
            // deferred final flush no-ops and silently drops the buffered events. Marshaling also
            // keeps _shutdownCalled a genuinely main-thread-only field.
            if (OS.GetThreadCallerId() != OS.GetMainThreadId())
            {
                Callable.From(Shutdown).CallDeferred();
                return;
            }
            try
            {
                if (!_initialized) return;
                // Stop accepting new events BEFORE taking the final snapshot. _initialized
                // is volatile and Track() bails on it, so a producer (possibly on a
                // background thread) cannot enqueue an event after this point that the
                // drain's DequeueAll would then miss and silently drop. (A Track already
                // past its _initialized check when the flag flips is an inherent last-
                // instant lock-free race and is unchanged by this ordering.) Latch the
                // shutdown too so a deferred tree entry cannot auto-init and revive the
                // instance mid-drain.
                _initialized = false;
                _shutdownCalled = true;
                // Final flush must be SYNCHRONOUS: _ExitTree tears the tree down in the
                // same call, so an async Flush() would resume after the node (and its
                // HttpRequest child) left the tree and drop the buffered events (the final
                // perf_heartbeat). Drain via the transport's blocking HttpClient path
                // instead, bounded so quit is never hung. Godot still has no offline queue
                // (that is UE5-only), so a send that fails within the budget is lost --
                // best-effort by design.
                DrainBlocking();
                GD.Print("[Framedash] SDK shut down.");
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] Shutdown() failed: {e.Message}");
            }
        }

        public override void _ExitTree()
        {
            if (s_instance == this)
            {
                Shutdown();
                s_instance = null;
            }
        }

        // Lifecycle notifications mirror Unity's OnApplicationPause / OnApplicationQuit
        // intent. Each handler is wrapped so no exception escapes the engine callback.
        public override void _Notification(int what)
        {
            try
            {
                switch (what)
                {
                    // App moved to background (mobile) — flush so a backgrounded/killed
                    // app does not lose buffered events. Unity: OnApplicationPause(true).
                    case (int)NotificationApplicationPaused:
                        Flush();
                        break;

                    // The window/OS requested a close. This is NOT necessarily terminal:
                    // a game may set SceneTree.AutoAcceptQuit=false to show a confirm
                    // dialog or reject the close, so draining here would block the main
                    // thread and permanently drop events even though the app keeps running.
                    // The terminal drain runs from _ExitTree/Predelete (Shutdown) once the
                    // quit is actually accepted, so nothing is needed here. Unity:
                    // OnApplicationQuit precursor.
                    case (int)NotificationWMCloseRequest:
                        break;

                    // Node is about to be deleted — final best-effort flush. Pairs with
                    // _ExitTree; whichever fires first runs Shutdown, the second is a
                    // no-op (Shutdown returns early once !_initialized).
                    case (int)NotificationPredelete:
                        Shutdown();
                        break;
                }
            }
            catch (Exception e)
            {
                GD.PushError($"[Framedash] _Notification({what}) failed: {e.Message}");
            }
        }
    }
}
