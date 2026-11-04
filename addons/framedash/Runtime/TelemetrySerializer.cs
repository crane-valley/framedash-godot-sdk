namespace Framedash
{
    public static class TelemetrySerializer
    {
        public static byte[] Serialize(TelemetryEvent[] events)
        {
            using (var batch = new ProtobufWriter(events.Length * 256))
            using (var eventWriter = new ProtobufWriter(256))
            using (var subWriter = new ProtobufWriter(64))
            {
                for (int i = 0; i < events.Length; i++)
                {
                    eventWriter.Reset();
                    WriteEvent(eventWriter, ref events[i], subWriter);
                    batch.WriteSubMessage(1, eventWriter);
                }

                return batch.ToArray();
            }
        }

        private static void WriteEvent(ProtobufWriter w, ref TelemetryEvent e, ProtobufWriter sub)
        {
            w.WriteString(1, e.EventName);

            w.WriteInt64(2, e.TimestampUs);

            w.WriteString(3, e.SessionId);

            w.WriteString(4, e.PlayerId);

            if (e.PositionX != 0f || e.PositionY != 0f || e.PositionZ != 0f)
            {
                sub.Reset();
                sub.WriteFloat(1, e.PositionX);
                sub.WriteFloat(2, e.PositionY);
                sub.WriteFloat(3, e.PositionZ);
                w.WriteSubMessage(5, sub);
            }

            w.WriteString(6, e.MapId);


            w.WriteFloat(8, e.Fps);

            w.WriteFloat(9, e.FrameTimeMs);

            w.WriteInt64(10, e.MemoryUsedBytes);

            w.WriteFloat(11, e.GpuTimeMs);

            if (e.Attributes != null)
            {
                for (int j = 0; j < e.Attributes.Count; j++)
                {
                    sub.Reset();
                    sub.WriteString(1, e.Attributes[j].Key);
                    sub.WriteString(2, e.Attributes[j].Value);
                    w.WriteSubMessage(12, sub);
                }
            }

            if (e.Metrics != null)
            {
                for (int j = 0; j < e.Metrics.Count; j++)
                {
                    sub.Reset();
                    sub.WriteString(1, e.Metrics[j].Key);
                    sub.WriteDouble(2, (double)e.Metrics[j].Value);
                    w.WriteSubMessage(13, sub);
                }
            }

            w.WriteEnum(14, (int)e.Source);

            w.WriteString(15, e.BuildId);

            w.WriteString(16, e.Platform);

            w.WriteString(17, e.EngineVersion);

            // fields 18-19: optional camera_yaw / camera_pitch. Single enforcement
            // point for the ingest invariants -- written together or not at all
            // (a present-mismatch is rejected) and only when both are finite
            // (NaN/Inf rejected). WriteFloatPresent emits even when the value is 0
            // (yaw 0 = North is a real value, unlike the zero-skipping WriteFloat).
            if (e.CameraYaw.HasValue
                && e.CameraPitch.HasValue
                && !float.IsNaN(e.CameraYaw.Value)
                && !float.IsInfinity(e.CameraYaw.Value)
                && !float.IsNaN(e.CameraPitch.Value)
                && !float.IsInfinity(e.CameraPitch.Value))
            {
                w.WriteFloatPresent(18, e.CameraYaw.Value);
                w.WriteFloatPresent(19, e.CameraPitch.Value);
            }

            w.WriteFloat(20, e.GameThreadMs);

            w.WriteFloat(21, e.RenderThreadMs);
        }
    }
}
