using Godot;

namespace Framedash
{
    /// <summary>
    /// Thread-safe ring buffer for telemetry events.
    /// When full, oldest events are dropped (game perf > telemetry completeness).
    /// </summary>
    public sealed class EventBuffer
    {
        private readonly TelemetryEvent[] _buffer;
        private readonly object _lock = new object();
        private int _head;
        private int _tail;
        private int _count;
        private int _droppedCount;
        public static readonly int DefaultCapacity = 10000;
        private const int DropLogInterval = 100;

        public int Count
        {
            get { lock (_lock) return _count; }
        }

        public int Capacity => _buffer.Length;

        public EventBuffer(int capacity)
        {
            if (capacity <= 0)
            {
                GD.PushWarning($"[Framedash] EventBuffer capacity must be > 0. Using default {DefaultCapacity}.");
                capacity = DefaultCapacity;
            }
            _buffer = new TelemetryEvent[capacity];
        }

        /// <summary>Add an event. Drops oldest if full.</summary>
        public void Enqueue(TelemetryEvent evt)
        {
            lock (_lock)
            {
                _buffer[_tail] = evt;
                _tail = (_tail + 1) % _buffer.Length;

                if (_count == _buffer.Length)
                {
                    // Ring buffer full — advance head (drop oldest)
                    _head = (_head + 1) % _buffer.Length;
                    _droppedCount++;

                    if (_droppedCount % DropLogInterval == 1)
                    {
                        GD.PushWarning($"[Framedash] Event buffer full — {_droppedCount} event(s) dropped so far.");
                    }
                }
                else
                {
                    _count++;
                }
            }
        }

        /// <summary>Dequeue all buffered events and reset.</summary>
        public TelemetryEvent[] DequeueAll()
        {
            lock (_lock)
            {
                if (_count == 0) return System.Array.Empty<TelemetryEvent>();

                var result = new TelemetryEvent[_count];
                for (int i = 0; i < _count; i++)
                {
                    int idx = (_head + i) % _buffer.Length;
                    result[i] = _buffer[idx];
                    _buffer[idx] = default; // Release references for GC
                }

                _head = 0;
                _tail = 0;
                _count = 0;

                return result;
            }
        }
    }
}
