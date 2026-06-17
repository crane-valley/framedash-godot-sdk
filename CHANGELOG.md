# Changelog

All notable changes to the Framedash Godot SDK are documented here. This project
follows [Keep a Changelog](https://keepachangelog.com/) and
[Semantic Versioning](https://semver.org/).

## [Unreleased]

- Centralize per-event field clamping into a shared `FieldClamp` helper (parity
  with the Unity and UE5 SDKs): event name, map_id, build_id, position,
  platform, engine_version, and attribute/metric caps are all clamped to the
  ingest limits before buffering, so one over-limit field can no longer make the
  consumer drop the whole 202-accepted flush.
- String truncation is now surrogate-pair safe (a split astral character is
  dropped rather than leaving a lone surrogate); player_id shares the same path.
- Clamp `memory_used_bytes` to the ingest range [0, 64 GiB] (previously only the
  negative floor was applied).
- Derive FPS from the raw frame delta instead of the clamped frame time, so a
  long (>10s) frame reports its true low rate rather than 0.1 fps.

## [0.1.0] - 2026-06-14

Initial public pre-release (beta).

- Godot 4.x C# telemetry SDK: `TelemetrySDK.Initialize(...)` and
  `TelemetrySDK.Instance.Track(...)`, registered as an autoload singleton via the
  editor plugin.
- Automatic performance collection (FPS, frame time, memory, GPU/render/process
  time) and session lifecycle.
- Batched, gzip-compressed Protobuf HTTP transport with retry and batch-splitting.
- Hand-written Protobuf serialization shared with the Unity SDK (no codegen
  dependency).
