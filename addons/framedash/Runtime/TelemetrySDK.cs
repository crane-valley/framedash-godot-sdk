// Pre-nullable-annotation file: its reference fields are lifecycle-initialized in
// InitializeInternal, not a constructor, so under a Nullable=enable consumer (and the
// EditorCompile harness) the unannotated declarations would raise CS8618-family noise.
// Pinning the context disabled keeps the addon warning-free in any host project without
// a risky wholesale annotation pass; new files should enable nullable instead.
#nullable disable

using System;
using System.Threading;
using System.Collections.Generic;
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
        private sealed class PendingInitialization
        {
            internal readonly string ApiKey;
            internal readonly string EndpointUrl;
            internal readonly string BuildId;
            internal readonly string PlayerId;

            internal PendingInitialization(
                string apiKey,
                string endpointUrl,
                string buildId,
                string playerId)
            {
                ApiKey = apiKey;
                EndpointUrl = endpointUrl;
                BuildId = buildId;
                PlayerId = playerId;
            }
        }
        // Another autoload can initialize Framedash before this node enters the tree.
        // Keep that one request until the selected singleton reaches _Ready.
        private static PendingInitialization s_pendingInitialization;
        private bool _autoCreated;
        // A registered autoload can enter after callers have already received the fallback.
        // It must stay in the tree, but cannot run a second telemetry session.
        private bool _inactiveDuplicate;
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
        public const string SdkVersion = "0.1.9";
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
        [Export] public int MaxPayloadBytes { get; set; } = 102400;
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
        // Prevents _Ready's auto-init from REVIVING a shut-down instance. Critical for an
        // auto-created node (no autoload) whose DEFERRED tree entry races a same-frame
        // Initialize + Shutdown(): without this, when the node finally enters the tree _Ready
        // would see _initialized == false + a non-empty (Initialize-set) ApiKey and
        // re-initialize, disposing the transport out from under the parked best-effort final
        // flush (it would then send on a freed HttpRequest). Cleared on a genuine re-init.
        // Written/read on the main thread only.
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
        // path. Always cleared once that flush RESOLVES (delivered or not); on a failure the
        // UNCONFIRMED subset (not the whole array -- a delivered split child must never be
        // resent) is folded back into the ring buffer and this slot nulled, so the next
        // Flush() can never silently overwrite an unconfirmed batch (no early loss). While the
        // flush is still parked this slot holds the whole array; its two racing consumers
        // (shutdown DrainBlocking, FlushBlocking seize) send it NON-splittable so a partially-
        // delivered parked batch's delivered children are never reshaped/duplicated.
        // Main-thread only: Flush() marshals to the main thread and the FlushAsync
        // continuation resumes there too.
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

        public bool IsInitialized => _initialized;

        private static bool IsAutoloadRegistered()
        {
            try
            {
                return ProjectSettings.HasSetting("autoload/Framedash");
            }
            catch
            {
                // A ProjectSettings probe must never make telemetry disrupt startup.
                return false;
            }
        }

        /// <summary>
        /// Singleton instance. On the main thread it is auto-created and added to the
        /// scene tree root if needed, mirroring the Unity SDK's auto-create. Two checks
        /// run before that and each returns null, in this order: a first access from a
        /// background thread (creating a Node off the main thread is unsafe), then no
        /// running SceneTree (e.g. a tool/editor context with no main loop). An instance
        /// that already exists is returned on any thread.
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

        private void ApplyInitialization(PendingInitialization initialization)
        {
            ApiKey = initialization.ApiKey;
            if (!string.IsNullOrEmpty(initialization.EndpointUrl))
                EndpointUrl = initialization.EndpointUrl;
            if (!string.IsNullOrEmpty(initialization.BuildId))
                BuildId = initialization.BuildId;
            if (initialization.PlayerId != null)
                PlayerId = initialization.PlayerId;
        }

        private static bool QueueInitializationForRegisteredAutoload(
            string apiKey,
            string endpointUrl,
            string buildId,
            string playerId)
        {
            lock (s_instanceLock)
            {
                if (s_instance != null || !IsAutoloadRegistered()) return false;
                if (s_pendingInitialization != null)
                {
                    GD.PushWarning(
                        "[Framedash] Initialization is already waiting for the registered autoload; duplicate request ignored.");
                    return true;
                }
                s_pendingInitialization =
                    new PendingInitialization(apiKey, endpointUrl, buildId, playerId);
                return true;
            }
        }

        /// <summary>
        /// Call this once at game startup (e.g., in a boot scene/autoload). Main-thread only:
        /// initialization creates scene-tree nodes (the transport's HttpRequest child), so a
        /// background-thread call is refused with a logged error.
        ///
        /// The Framedash ingest API key. Unlike the other parameters this one is written
        /// UNCONDITIONALLY, so passing null or empty CLEARS a key set on the `ApiKey` inspector
        /// property rather than falling back to it, and initialization then fails with a logged
        /// error. There is also no `FRAMEDASH_API_KEY` environment fallback; the Unity SDK has
        /// one.
        ///
        /// Ingest endpoint; null/empty keeps the current value. Build identifier; null/empty
        /// keeps the current value. Developer-supplied player identifier; null keeps the
        /// current value.
        ///
        /// `null` only when no instance can be produced: a first access from a background
        /// thread, or no running SceneTree. On the main thread with a tree, `Instance`
        /// auto-creates a fallback node rather than returning null. After a null return the
        /// configuration is queued only if the plugin autoload is registered, and applied once
        /// that node is ready; with no autoload the call is dropped. Otherwise the singleton,
        /// which on a failed initialization is returned with `IsInitialized` still false rather
        /// than as null. On an already-initialized SDK the call is ignored (warn) and the
        /// existing instance is returned unchanged; call `Shutdown` first to reconfigure.
        /// </summary>
        public static TelemetrySDK Initialize(string apiKey, string endpointUrl = null, string buildId = null, string playerId = null)
        {
            var sdk = Instance;
            if (sdk == null)
            {
                QueueInitializationForRegisteredAutoload(
                    apiKey,
                    endpointUrl,
                    buildId,
                    playerId);
                return null;
            }
            if (sdk.IsInitialized)
            {
                // Reject BEFORE mutating config so a duplicate Initialize() cannot leave the
                // live session sending with the old key/endpoint but new BuildId/PlayerId.
                GD.PushWarning("[Framedash] SDK is already initialized; Initialize() ignored. Call Shutdown() first to reconfigure.");
                return sdk;
            }
            sdk.ApplyInitialization(
                new PendingInitialization(apiKey, endpointUrl, buildId, playerId));
            sdk.InitializeInternal();
            return sdk;
        }

        private void CopyConfigurationFrom(TelemetrySDK source)
        {
            ApiKey = source.ApiKey;
            EndpointUrl = source.EndpointUrl;
            BuildId = source.BuildId;
            PlayerId = source.PlayerId;
            CaptureCameraRotation = source.CaptureCameraRotation;
            MaxBatchSize = source.MaxBatchSize;
            EventBufferCapacity = source.EventBufferCapacity;
            FlushIntervalSeconds = source.FlushIntervalSeconds;
            MaxPayloadBytes = source.MaxPayloadBytes;
            HttpTimeoutSeconds = source.HttpTimeoutSeconds;
            MaxRetries = source.MaxRetries;
            VerboseLogging = source.VerboseLogging;
            SamplingRate = source.SamplingRate;
        }

        public override void _EnterTree()
        {
            bool hasExistingInstance = s_instance != null && s_instance != this;
            bool incomingIsRegisteredAutoload = !_autoCreated && IsAutoloadRegistered();
            AutoloadCollisionDecision decision = GodotRuntimePolicy.DecideAutoloadCollision(
                hasExistingInstance,
                hasExistingInstance && s_instance._autoCreated,
                incomingIsRegisteredAutoload);

            if (decision == AutoloadCollisionDecision.PreserveExistingFallback)
            {
                // Instance already published the fallback to callers, so replacing or
                // freeing it would invalidate a live public reference. The registered
                // autoload remains attached as an inert duplicate for editor ownership.
                TelemetrySDK fallback = s_instance;
                if (!fallback._initialized && !fallback._shutdownCalled)
                {
                    fallback.CopyConfigurationFrom(this);
                    if (!string.IsNullOrEmpty(fallback.ApiKey))
                    {
                        fallback.InitializeInternal();
                    }
                }
                _inactiveDuplicate = true;
                return;
            }
            if (decision == AutoloadCollisionDecision.RejectIncoming)
            {
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
            if (_inactiveDuplicate) return;

            PendingInitialization pending = null;
            lock (s_instanceLock)
            {
                if (s_instance == this)
                {
                    pending = s_pendingInitialization;
                    s_pendingInitialization = null;
                }
            }
            if (pending != null)
            {
                ApplyInitialization(pending);
            }
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
                // GetViewport() returns null when this node is not currently inside the tree
                // (transient teardown / tool contexts), so skip the viewport-dependent metrics
                // rather than dereferencing null.
                Viewport viewport = GetViewport();
                if (viewport != null)
                {
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

    }
}
