# Framedash Godot SDK

Godot 4.3+ C# addon for collecting game telemetry and sending it to the Framedash platform.

## Requirements

- Godot 4.3 or newer with the **.NET (C#)** build (the editor download labeled "mono"). The standard (GDScript-only) Godot build cannot compile the addon's C# sources. Developed and verified against Godot 4.6.3.
- .NET 8 SDK (the addon targets `net8.0` via GodotSharp).

## Compatibility

- **Primary target:** Godot 4.6.x (the current stable line) — the version the SDK is developed and verified against.
- **Minimum:** Godot 4.3. The SDK uses only long-stable Godot 4.x APIs, but Godot's minor releases can introduce breaking changes, so smoke-test against the exact Godot version you ship on.
- **Export targets:** Desktop (Windows / macOS / Linux), Android, and iOS. Web (HTML5) export is not supported for the C# build (Godot's .NET web export is experimental).
- Some C# export-toolchain requirements are set by Godot, not this SDK — for example, C# Android export on Godot 4.5+ requires the .NET 9 SDK at build time even though the addon itself targets `net8.0`.

## Installation

1. Copy the addon's runtime files into your Godot project at `res://addons/framedash/` — the `Runtime/` folder, `FramedashEditorPlugin.cs`, and `plugin.cfg`. **Do not copy the `Tests/` folder** (see the warning below).
2. Open **Project > Project Settings > Plugins** and enable **Framedash Telemetry SDK**.

Enabling the plugin registers a **Framedash** autoload singleton automatically, so the SDK node is created and persists for the lifetime of the game. There is no separate scene wiring to do.

> The addon's C# files compile into your project's own assembly (C# Godot addons do not ship a runtime `.csproj`). After enabling the plugin, build the project once (**Build** in the editor) so the autoload type is available.

> **Do not ship the `Tests/` folder in your game.** It holds NUnit test sources (and a `Godot.GD` stub) used only for SDK development. A Godot .NET project compiles every `.cs` under the project into the game assembly, so copying `Tests/` would pull in NUnit packages your project does not have and a stub that conflicts with the real `Godot.GD` type, breaking the build. `Tests/` is for running the engine-independent unit tests with `dotnet test` from the SDK repo, not for shipping.

## Components

| File | Description |
|------|-------------|
| `TelemetrySDK.cs` | Main entry point — initialization, configuration, session lifecycle, autoload `Node` |
| `TelemetryEvent.cs` | Event data model |
| `TelemetrySerializer.cs` | Event serialization |
| `ProtobufWriter.cs` | Protobuf binary encoding |
| `TransportLayer.cs` | HTTPS transport with gzip compression |
| `SessionManager.cs` | Session ID and metadata management |
| `PerformanceCollector.cs` | Automatic FPS, frame time, memory, GPU/render/process time collection |
| `SamplingPolicy.cs` | Configurable event sampling and throttling |
| `EventBuffer.cs` | Batched event buffering before transmission, default 10,000 events |
| `IoStats.cs` | Thread-safe accumulator for manually-reported disk I/O samples (`ReportIoSample`) |

## Automatic Events

The SDK automatically sends the following events with `Source=Automated`. These bypass sampling policy and always fire regardless of the configured sampling rate.

| Event | Trigger | Description |
|-------|---------|-------------|
| `session_start` | Once, on initialization | Guarantees the backend sees at least one event per session |
| `perf_heartbeat` | Every 10 seconds | Continuous performance baseline (FPS, frame time, memory, GPU/render/process time) |

Both events include full performance metrics from `PerformanceCollector`.

## Performance Collection

`PerformanceCollector.cs` reads from Godot's engine APIs:

- **FPS and frame time** — computed from the real (wall-clock) frame delta measured via `Time.GetTicksUsec()`, so the values are independent of any in-game time scaling (`Engine.TimeScale`).
- **Memory** — `Performance.MemoryStatic` for the static memory used by the engine.
- **GPU and render-CPU time** — measured via `RenderingServer` (the GPU and CPU times the renderer reports for the last frame), converted to milliseconds.
- **Game-thread (process) time** — `Performance.TimeProcess` for the time spent in the game's `_process` step.

Any metric that the platform or build cannot report is left as `0`, which the wire contract treats as "not collected" (it is not a measured zero).

## Disk I/O Metrics (Manual Feed)

Godot exposes no engine-level disk I/O counters, so unlike the Unity/UE5 SDKs
this SDK collects `io.*` metrics **only** if your game reports them. Call
`ReportIoSample` from your own loader/VFS code as reads complete; the SDK
accumulates a window and attaches it to the next `perf_heartbeat` as
`metrics["io.read_bytes"]`, `metrics["io.read_time_ms"]`, and
`metrics["io.read_ops"]` (window deltas since the previous heartbeat, then
reset). If `ReportIoSample` is never called, these keys are simply absent from
`perf_heartbeat` -- absent means "not collected", not a measured zero. Safe to
call from any thread; never throws.

```csharp
using Godot;

// Poll a threaded resource load and report the elapsed time as one I/O sample
// once it finishes. Adapt bytes/ops to whatever your loader actually knows.
ResourceLoader.LoadThreadedRequest(path);
ulong startUsec = Time.GetTicksUsec();

// ... on a later frame, once ResourceLoader.LoadThreadedGetStatus(path)
// reports ThreadLoadStatus.Loaded ...
float elapsedMs = (Time.GetTicksUsec() - startUsec) / 1000f;
Framedash.TelemetrySDK.Instance.ReportIoSample(
    bytes: estimatedFileSizeBytes,
    readTimeMs: elapsedMs,
    ops: 1);
```

## Map/Level Load-Time

Measure how long a scene/level takes to load and emit it as a `map_load` event.
The load time rides the generic metrics map (`load_time_ms`) and the loaded map
name rides the attributes map as `attributes["map_name"]`. `map_id` is left **empty**
on purpose (like `perf_heartbeat`): a `map_load` has no world position, so an empty
`map_id` keeps it out of the spatial heatmap and the activation gate, which key on a
non-empty `map_id`. There is no dedicated proto or ClickHouse column yet (web/CLI
charts, grouped by `attributes['map_name']`, and `perf-diff` gating land in a
follow-up PR). Query it today via the data-export / query REST API (e.g.
`metrics['load_time_ms']`). The event flows through the normal `Track` path, so it is
sampled and buffered like any other event.

```csharp
// Time a load with the built-in timer:
Framedash.TelemetrySDK.Instance.BeginMapLoad("world_1");
// ... load the scene ...
Framedash.TelemetrySDK.Instance.EndMapLoad();   // emits map_load (map_name="world_1", load_time_ms=elapsed)

// Or report a time you measured yourself (custom/streaming loaders):
Framedash.TelemetrySDK.Instance.ReportMapLoad("world_1", loadTimeMs: 842.0);
```

The timer uses a monotonic wall clock, so a paused tree or changed
`Engine.TimeScale` does not distort the measurement. Calling `BeginMapLoad` again
before `EndMapLoad` replaces the pending measurement; `EndMapLoad` with no pending
`BeginMapLoad` is a no-op. A NaN/Infinity/negative `ReportMapLoad` time is dropped
(not clamped). All three methods are safe to call from any thread, never throw, and
are no-ops before `Initialize()`.

## Camera Direction

When **Capture Camera Rotation** is enabled (the default), every event records the active `Camera3D`'s yaw and pitch, which powers the direction breakdown on the heatmap cell-detail view. The yaw/pitch are derived from the camera's forward vector. Yaw is normalized to `[0, 360)` and increases clockwise; the direction chart labels yaw 0 as North, with the engine's default forward axis as that reference (a game world has no geographic North, so the compass labels are relative). Pitch is `[-90, 90]` (+90 = looking up).

Capture is skipped whenever there is no active `Camera3D` — for example a 2D-only game or a headless build — so the camera fields are simply omitted from those events.

Disable it by unchecking **Capture Camera Rotation** on the autoload's inspector fields, or from code:

```csharp
Framedash.TelemetrySDK.Instance.CaptureCameraRotation = false;
```

## Quick Start

### 1. Enable the plugin

Enable **Framedash Telemetry SDK** under **Project > Project Settings > Plugins** (see Installation). This registers the **Framedash** autoload.

### 2. Initialize once at startup

```csharp
Framedash.TelemetrySDK.Initialize("your-api-key");
// optional 2nd arg endpointUrl, 3rd arg buildId:
// Framedash.TelemetrySDK.Initialize("your-api-key", "https://ingest.framedash.dev/v1/events", "1.0.0");
```

### 3. Track gameplay events

```csharp
Framedash.TelemetrySDK.Instance.Track("player_death", "map_01", playerNode.GlobalPosition);
```

### 4. Set the player ID after login (optional)

```csharp
Framedash.TelemetrySDK.Instance.SetPlayerId(playerId);
```

### 5. Sampling

The SDK applies a global sampling rate (default `1.0` = keep all). High-frequency events can opt into a lower per-event-name rate that overrides the global rate at runtime:

```csharp
Framedash.TelemetrySDK.Instance.SetEventSamplingRate("ai_pathfind_step", 0.05f); // ~5%
Framedash.TelemetrySDK.Instance.RemoveEventSamplingRate("ai_pathfind_step");      // back to global
```

Automatic events (`session_start`, `perf_heartbeat`) bypass sampling.

> **Configuration is code-first.** The plugin registers a *script* autoload, which Godot instantiates from `TelemetrySDK.cs` using the code defaults — its `[Export]` fields are **not** editable from Project Settings, so configure the SDK by calling `TelemetrySDK.Initialize(...)` (typically from an early `_Ready()`). The `[Export]` fields (API key, endpoint, sampling rate, capture-camera-rotation, etc.) become editable only if you instead add the `TelemetrySDK` script to a node in a scene you control (for example, your own autoload scene) and set them in the inspector there; if you set `ApiKey` that way, the node auto-initializes in `_Ready()` with no code.

### 6. Automated profiling sessions (CI)

For build-over-build performance gating, tag a run's events with build metadata
so the dashboard and `framedash perf-diff` can compare one build against another.
Call this once after `Initialize()` in your automated-test / profiling entry point:

```csharp
Framedash.TelemetrySDK.Instance.BeginAutomatedSession(
    buildId:  commitSha,       // -> the first-class build_id field
    branch:   "main",          // -> ci.branch attribute
    commit:   commitSha,       // -> ci.commit attribute
    scenario: "boot_to_menu"); // -> ci.scenario attribute

// ... run the scenario; gameplay + perf_heartbeat events are now tagged ...

Framedash.TelemetrySDK.Instance.Flush();
Framedash.TelemetrySDK.Instance.EndAutomatedSession();
```

`branch`, `commit`, and `scenario` ride in the existing event `attributes` map
(`ci.*`), so no schema change is required; the tags apply to every event,
including the automatic `perf_heartbeat` that carries the frame-time / memory /
GPU metrics. A per-event attribute with the same key overrides the session value.

If your CI harness exports the standard Framedash variables (`FRAMEDASH_BUILD_ID`,
`FRAMEDASH_GIT_BRANCH`, `FRAMEDASH_GIT_COMMIT`, `FRAMEDASH_TEST_SCENARIO`) -- the
planned `framedash run-profile-test` runner will export these for you -- call the
zero-argument overload instead:

```csharp
Framedash.TelemetrySDK.Instance.BeginAutomatedSessionFromEnvironment();
```

Then gate the build in CI with
`framedash perf-diff --baseline <old_build_id> --candidate <new_build_id> --fail-on-regression`.

Two things to know when wiring this into a real pipeline:

- `build_id` is the dimension `perf-diff` compares. It groups and compares by
  `build_id` (optionally narrowed by map/platform), not by `ci.scenario`, so two
  scenarios under one `build_id` fold into a single aggregate. To compare
  scenarios independently, give each its own `build_id` (for example
  `<commit>-<scenario>`) and treat `ci.scenario` as a queryable label rather than
  a `perf-diff` split key.
- The `ci.*` tags live in the event `attributes` map, which COPPA-redacted
  projects strip on ingest -- under COPPA only `build_id` survives. If you run
  automated profiling on a COPPA project, make `build_id` carry everything the
  comparison must distinguish.

## Local Development

To point the Godot SDK at a local Framedash stack:

1. Follow the root local setup guide to start the Docker services. Then create the local environment files from the checked-in examples:
   ```bash
   cp apps/web/.env.example apps/web/.env.local
   cp apps/ingest/.dev.vars.example apps/ingest/.dev.vars
   cp apps/consumer/.dev.vars.example apps/consumer/.dev.vars
   ```
   Edit these files as described in the root setup guide.
2. Start the ingest Worker locally:
   ```bash
   pnpm --filter @framedash/ingest dev
   ```
3. Initialize the SDK with the localhost ingest endpoint:
   ```csharp
   Framedash.TelemetrySDK.Initialize(
       "your-local-api-key",
       "http://localhost:8787/v1/events",
       "1.0.0"
   );
   ```

The local API key must exist in your local PostgreSQL `api_keys` table. Create one via the dashboard at `http://localhost:3000`.
