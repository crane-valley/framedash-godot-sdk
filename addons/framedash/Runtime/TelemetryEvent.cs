using System.Collections.Generic;

namespace Framedash
{
    /// <summary>Source of the telemetry event.</summary>
    public enum TelemetrySource
    {
        Unspecified = 0,
        Player = 1,
        Automated = 2,
    }

    /// <summary>Serializable key-value pair for string attributes.</summary>
    [System.Serializable]
    public struct StringPair
    {
        public string Key;
        public string Value;
        public StringPair(string key, string value) { Key = key; Value = value; }
    }

    /// <summary>Serializable key-value pair for float metrics.</summary>
    [System.Serializable]
    public struct FloatPair
    {
        public string Key;
        public float Value;
        public FloatPair(string key, float value) { Key = key; Value = value; }
    }

    /// <summary>A single telemetry event ready for batching and serialization.</summary>
    [System.Serializable]
    public struct TelemetryEvent
    {
        public string EventName;
        public long TimestampUs;
        public string SessionId;
        public string PlayerId;
        public float PositionX;
        public float PositionY;
        public float PositionZ;
        public string MapId;
        public float Fps;
        public float FrameTimeMs;
        public long MemoryUsedBytes;
        public float GpuTimeMs;
        public TelemetrySource Source;
        public string BuildId;
        public string Platform;
        public string EngineVersion;
        public List<StringPair> Attributes;
        public List<FloatPair> Metrics;
        public float GameThreadMs;
        public float RenderThreadMs;
        public float? CameraYaw;
        public float? CameraPitch;
    }
}
