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
