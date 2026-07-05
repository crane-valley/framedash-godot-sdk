# Changelog

All notable changes to the Framedash Godot SDK are documented here. This project
follows [Keep a Changelog](https://keepachangelog.com/) and
[Semantic Versioning](https://semver.org/).

## [Unreleased]

## [0.1.2] - 2026-07-05

- Prefer-IPv4-with-IPv6-fallback ingest connect (parity with the Unity SDK).
  Godot's HTTP stack has no Happy Eyeballs, so on a broken-IPv6 network (a
  global AAAA advertised via Router Advertisement with no working IPv6 route)
  an OS-resolver AAAA-first pick wedged every flush connect for the full HTTP
  timeout and the batch was dropped -- silent telemetry loss. The transport now
  resolves the endpoint per address family on Godot's native async resolver
  (polled from short awaited timers with a ~3s cap, so the game thread is never
  blocked) and points Godot's `HttpRequest` at the pinned IP literal, IPv4
  first. SNI
  and full certificate verification are preserved by overriding the TLS common
  name back to the original FQDN (`TlsOptions.Client` common-name override; the
  trusted CA bundle stays the default), and an explicit `Host: <fqdn>` header
  keeps HTTP routing on the hostname rather than the IP literal. The address
  family toggles to IPv6 only when an attempt fails at the transport level
  (status 0 -- never on a real HTTP response, and not on a local dispatch
  error), and toggles back, so broken-IPv6 networks deliver over IPv4 while
  IPv6-only networks still deliver. Loopback, IP-literal, and plain-HTTP
  endpoints pass through untouched, and a failed/timed-out resolution falls
  back to the previous hostname-URL behavior for that flush and is retried on a
  later one. Family-ordering and URL-rewrite logic lives in a pure,
  NUnit-tested `EndpointAddressPlanner`; the shared flush budget, single-flight
  guard, and backoff are unchanged.
- Bound the flush retry ladder so a wedged endpoint can no longer starve later
  flushes (F49). Godot/.NET `HttpClient` has no Happy Eyeballs, so an IPv6
  blackhole wedges the connect for the full HTTP timeout; the single-flight flush
  guard was held across all 5 retries x 30s + backoff (~2.5 min), skipping every
  later flush (heartbeats, session_end, the shutdown flush) and turning one stuck
  batch into silent total data loss for short sessions. The per-attempt HTTP
  timeout is now 10s (was 30s) and the retry budget is 3 attempts (was 5), so the
  worst-case flush wall-time is ~33s. The retry budget is a single deadline SHARED
  across all split children of one flush, and each request's timeout is capped to
  the remaining budget, so the bound is honored exactly regardless of split depth.
  Both are configurable in the Inspector via the new `HttpTimeoutSeconds` and
  `MaxRetries` exports (clamped to sane maxima of 60s / 10 attempts so a
  misconfiguration cannot recreate unbounded starvation).
- The SDK version reported via `X-SDK-Version` is now a code constant instead of
  an `[Export]` property. An exported version was captured into saved scenes /
  autoload state, so upgrading the addon kept sending the OLD version from the
  saved value; the constant always matches the installed addon.
- Add an opt-in verbose success log (F25): with the new `VerboseLogging` export
  enabled, each accepted batch logs "Flushed N events (HTTP 202)" so first-time
  integrators can positively confirm delivery client-side. Off by default.
- Annotate the engine-independent Runtime sources with C# nullable reference
  types (F23). A Godot C# addon's `.cs` files compile directly into the host
  project, so a project building with `Nullable=enable` previously saw CS86xx
  warnings from the SDK; those are now resolved except for 4 in
  `ProtobufWriter.cs`, which is kept byte-identical with the Unity SDK as a
  wire-contract file. Annotation-only; no runtime behavior change.

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
