#nullable enable

using System;

namespace Framedash
{
    /// <summary>
    /// Engine-independent state machine + math for the map/level load-time helper
    /// (TelemetrySDK.BeginMapLoad / EndMapLoad / ReportMapLoad). Holds the pending
    /// measurement (loaded map name + a monotonic start timestamp in seconds) and
    /// computes the elapsed load time in milliseconds. The SDK reads its monotonic
    /// wall clock (System.Diagnostics.Stopwatch) and feeds the seconds in, so the
    /// timing math is unit-testable without an engine -- no Godot references,
    /// NUnit-tested. Kept identical to the Unity SDK's MapLoadTimer. Thread-safe (a
    /// lock guards the pending pair) so a Begin/End that straddles a threaded loader
    /// observes a coherent state, matching the SDK's thread-safe public-API convention.
    ///
    /// The load time rides the existing metrics map (proto field 13) as
    /// <see cref="KeyLoadTimeMs"/> on the auto event <see cref="MapLoadEventName"/>,
    /// with the loaded map name carried on the ATTRIBUTES map -- no proto/ClickHouse
    /// change (mirrors the io.* attributes-map guardrail).
    ///
    /// map_id is deliberately left EMPTY (like perf_heartbeat): a map_load carries no
    /// spatial position, so an empty map_id keeps it out of the spatial heatmap grid
    /// query and the activation gate (both key on a non-empty map_id). The loaded map
    /// name rides <see cref="KeyMapName"/> in the attributes map instead; the follow-up
    /// web/CLI PR groups load-time charts by attributes['map_name'].
    /// </summary>
    public sealed class MapLoadTimer
    {
        /// <summary>Auto event name emitted for a completed map/level load.</summary>
        public const string MapLoadEventName = "map_load";

        /// <summary>
        /// Attributes-map key carrying the loaded map name (map_id stays empty so the
        /// event is not treated as spatial or activation-qualifying).
        /// </summary>
        public const string KeyMapName = "map_name";

        /// <summary>Metrics-map key carrying the measured load time in milliseconds.</summary>
        public const string KeyLoadTimeMs = "load_time_ms";

        private readonly object _lock = new object();
        private string _pendingMapName = "";
        private double _startSeconds;
        private bool _pending;

        /// <summary>
        /// Begin (or replace) a pending measurement. Calling Begin again before
        /// <see cref="End"/> REPLACES the pending measurement -- the earlier start is
        /// discarded and only the most recent Begin/End pair is reported. A null map
        /// name is stored as empty.
        /// </summary>
        public void Begin(string? mapName, double startSeconds)
        {
            lock (_lock)
            {
                _pendingMapName = mapName ?? "";
                _startSeconds = startSeconds;
                _pending = true;
            }
        }

        /// <summary>
        /// Complete a pending measurement and clear it. Returns false (a no-op) when no
        /// Begin is pending. On success <paramref name="elapsedMs"/> is the load time in
        /// milliseconds, floored at 0 so a backwards monotonic-clock reading never yields
        /// a negative load time, and <paramref name="mapName"/> is the stored map name.
        /// </summary>
        public bool End(double endSeconds, out string mapName, out double elapsedMs)
        {
            lock (_lock)
            {
                if (!_pending)
                {
                    mapName = "";
                    elapsedMs = 0.0;
                    return false;
                }
                mapName = _pendingMapName;
                double ms = (endSeconds - _startSeconds) * 1000.0;
                elapsedMs = ms < 0.0 ? 0.0 : ms;
                _pendingMapName = "";
                _pending = false;
                return true;
            }
        }

        /// <summary>True while a Begin is awaiting its End.</summary>
        public bool HasPending
        {
            get { lock (_lock) return _pending; }
        }

        /// <summary>
        /// Validate a directly-reported load time (ReportMapLoad). A NaN, Infinity, or
        /// negative value is rejected so the whole call is DROPPED (not clamped),
        /// matching the drop-don't-clamp rule the other manual metric feeds use.
        /// </summary>
        public static bool IsValidLoadTimeMs(double loadTimeMs)
        {
            return !double.IsNaN(loadTimeMs) && !double.IsInfinity(loadTimeMs) && loadTimeMs >= 0.0;
        }
    }
}
