namespace Framedash
{
    /// <summary>
    /// Serializes <see cref="TelemetryEvent"/> arrays into Protobuf
    /// <c>TelemetryBatch</c> wire format compatible with
    /// <c>fromBinary(TelemetryBatchSchema, bytes)</c> on the ingest side.
    /// </summary>
    public static class TelemetrySerializer
    {
        /// <summary>
        /// Serialize events into a Protobuf TelemetryBatch message.
        /// </summary>
        /// <param name="events">Array of telemetry events to serialize.</param>
        /// <returns>Protobuf-encoded bytes ready for HTTP POST body.</returns>
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
                    // TelemetryBatch.events = field 1 (repeated)
                    batch.WriteSubMessage(1, eventWriter);
                }

                return batch.ToArray();
            }
        }

        private static void WriteEvent(ProtobufWriter w, ref TelemetryEvent e, ProtobufWriter sub)
        {
            // field 1: string event_name
            w.WriteString(1, e.EventName);

            // field 2: int64 timestamp_us
            w.WriteInt64(2, e.TimestampUs);

            // field 3: string session_id
            w.WriteString(3, e.SessionId);

            // field 4: string player_id
            w.WriteString(4, e.PlayerId);

            // field 5: Vector3 position (embedded message)
            // Skip entire sub-message if all components are zero
            // ReSharper disable CompareOfFloatsByEqualityOperator
            if (e.PositionX != 0f || e.PositionY != 0f || e.PositionZ != 0f)
            {
                sub.Reset();
                sub.WriteFloat(1, e.PositionX);
                sub.WriteFloat(2, e.PositionY);
                sub.WriteFloat(3, e.PositionZ);
                w.WriteSubMessage(5, sub);
            }
            // ReSharper restore CompareOfFloatsByEqualityOperator

            // field 6: string map_id
            w.WriteString(6, e.MapId);

            // field 7: reserved (was zone_id)

            // field 8: float fps
            w.WriteFloat(8, e.Fps);

            // field 9: float frame_time_ms
            w.WriteFloat(9, e.FrameTimeMs);

            // field 10: int64 memory_used_bytes
            w.WriteInt64(10, e.MemoryUsedBytes);

            // field 11: float gpu_time_ms
            w.WriteFloat(11, e.GpuTimeMs);

            // field 12: map<string,string> attributes
            // Proto3 map is encoded as repeated sub-message entries:
            //   message AttributesEntry { string key = 1; string value = 2; }
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

            // field 13: map<string,double> metrics
            // C# float is widened to proto double via implicit cast
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

            // field 14: TelemetrySource source (enum)
            w.WriteEnum(14, (int)e.Source);

            // field 15: string build_id
            w.WriteString(15, e.BuildId);

            // field 16: string platform
            w.WriteString(16, e.Platform);

            // field 17: string engine_version
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

            // field 20: float game_thread_ms
            w.WriteFloat(20, e.GameThreadMs);

            // field 21: float render_thread_ms
            w.WriteFloat(21, e.RenderThreadMs);
        }
    }
}
