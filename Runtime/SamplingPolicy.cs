#nullable enable

using System.Collections.Generic;

namespace Framedash
{
    /// <summary>
    /// Controls which events are sampled and at what rate.
    /// Supports a global default rate plus per-event-name overrides; if an override
    /// is set for a given event name it takes precedence, otherwise the global rate
    /// applies.
    /// </summary>
    public sealed class SamplingPolicy
    {
        private static readonly object s_randomLock = new object();
        private static readonly System.Random s_random = new System.Random();
        private readonly object _ratesLock = new object();
        private readonly Dictionary<string, float> _eventRates = new Dictionary<string, float>(System.StringComparer.Ordinal);
        private float _rate;

        /// <summary>Default sampling rate when no per-event override exists (0.0 = drop all, 1.0 = keep all).</summary>
        public float Rate
        {
            get { lock (_ratesLock) { return _rate; } }
            set { lock (_ratesLock) { _rate = System.Math.Clamp(value, 0f, 1f); } }
        }

        public SamplingPolicy(float rate = 1f)
        {
            Rate = rate;
        }

        /// <summary>
        /// Set a per-event-name sampling rate that overrides the global rate for the given event name.
        /// Null/empty event names are ignored. Rate is clamped to [0, 1].
        /// </summary>
        public void SetEventRate(string? eventName, float rate)
        {
            if (string.IsNullOrEmpty(eventName)) return;
            float clamped = System.Math.Clamp(rate, 0f, 1f);
            lock (_ratesLock) { _eventRates[eventName] = clamped; }
        }

        /// <summary>
        /// Remove a per-event-name override so the event falls back to the global rate.
        /// Returns true if an override was present.
        /// </summary>
        public bool RemoveEventRate(string? eventName)
        {
            if (string.IsNullOrEmpty(eventName)) return false;
            lock (_ratesLock) { return _eventRates.Remove(eventName); }
        }

        /// <summary>
        /// Returns true if this event should be kept. Uses a per-event-name rate if one is set
        /// for <paramref name="eventName"/>, otherwise falls back to the global rate.
        /// </summary>
        public bool ShouldSample(string? eventName)
        {
            float rate;
            lock (_ratesLock)
            {
                rate = _rate;
                if (!string.IsNullOrEmpty(eventName) && _eventRates.TryGetValue(eventName, out float perEvent))
                    rate = perEvent;
            }

            if (rate >= 1f) return true;
            if (rate <= 0f) return false;
            lock (s_randomLock) { return (float)s_random.NextDouble() <= rate; }
        }
    }
}
