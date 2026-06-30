# Changelog

All notable changes to the Framedash Godot SDK are documented here. This project
follows [Keep a Changelog](https://keepachangelog.com/) and
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.1] - 2026-06-30

- Automated profiling sessions for CI: `BeginAutomatedSession(buildId, branch,
  commit, scenario)` (and `BeginAutomatedSessionFromEnvironment()`, which reads the
  `FRAMEDASH_BUILD_ID` / `FRAMEDASH_GIT_BRANCH` / `FRAMEDASH_GIT_COMMIT` /
  `FRAMEDASH_TEST_SCENARIO` environment variables) tag every subsequent event with
  CI metadata so build-over-build performance can be compared in the dashboard and
  via `framedash perf-diff`. The build id is stamped as the first-class `build_id`
  field; branch, commit, and scenario ride in the existing attributes map as
  `ci.branch` / `ci.commit` / `ci.scenario` (no proto change). `EndAutomatedSession()`
  stops the tagging. The session tags merge into every event -- including the
  automatic `perf_heartbeat` that carries the performance metrics -- and a per-event
  attribute with the same key overrides the session value.
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
