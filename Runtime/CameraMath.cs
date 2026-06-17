using System;

namespace Framedash
{
    /// <summary>
    /// Pure camera-orientation conversions to the Framedash wire convention:
    /// yaw in [0, 360), pitch in [-90, 90] where +90 = looking up.
    /// Engine-independent so it can be unit-tested without a game engine.
    /// </summary>
    public static class CameraMath
    {
        /// <summary>Normalize a yaw angle (degrees) to [0, 360).</summary>
        public static float NormalizeYaw(float yawDegrees)
        {
            float r = yawDegrees % 360f;
            if (r < 0f) r += 360f;
            // A tiny negative r + 360f can round to exactly 360f; the wire range is
            // the half-open [0, 360), so fold 360 back to 0 (same heading).
            if (r >= 360f) r -= 360f;
            return r;
        }

        /// <summary>
        /// Derive wire-convention (yaw, pitch) from a camera forward direction vector.
        /// Godot is +Y up, -Z forward, right-handed. Yaw 0 = the default forward axis (-Z),
        /// increasing clockwise when viewed from above; pitch +90 = looking up. Returns
        /// (0, 0) for a zero-length vector. Engine-independent so it is NUnit-testable.
        /// </summary>
        public static void YawPitchFromForward(float fx, float fy, float fz, out float yaw, out float pitch)
        {
            float lenSq = fx * fx + fy * fy + fz * fz;
            if (lenSq <= 0f || float.IsNaN(lenSq) || float.IsInfinity(lenSq))
            {
                yaw = 0f;
                pitch = 0f;
                return;
            }
            float len = (float)Math.Sqrt(lenSq);
            // Heading: -Z is forward (yaw 0). atan2(x, -z) gives 0 at -Z, +90 at +X (right/East),
            // +180 at +Z, +270 at -X — clockwise from above. Normalize to [0, 360).
            float rawYaw = (float)(Math.Atan2(fx, -fz) * (180.0 / Math.PI));
            yaw = NormalizeYaw(rawYaw);
            // Pitch from the vertical component; +Y up => positive pitch (looking up).
            float ny = fy / len;
            if (ny > 1f) ny = 1f;
            if (ny < -1f) ny = -1f;
            float p = (float)(Math.Asin(ny) * (180.0 / Math.PI));
            if (p > 90f) p = 90f;
            if (p < -90f) p = -90f;
            pitch = p;
        }

        // A yaw quantum value outside the valid [0, 36000) range, used as the
        // "no camera this frame" sentinel in the high half of a packed snapshot.
        private const long AbsentYawQuantum = 0xFFFFFFFFL;

        /// <summary>The packed value meaning "no camera captured this frame".</summary>
        public const long CameraAbsent = AbsentYawQuantum << 32;

        /// <summary>
        /// Pack a finite (yaw, pitch) pair into one 64-bit value so the SDK can
        /// publish and read the pair atomically (Interlocked) across threads with
        /// no lock and no allocation. Values are quantized to 0.01 deg -- far finer
        /// than the 45-deg direction bins. High 32 bits = yaw in [0, 36000),
        /// low 32 bits = (pitch + 90) in [0, 18000]. Uses only int/float ops so it
        /// compiles on every Godot/.NET runtime (no BitConverter.SingleToInt32Bits).
        /// </summary>
        public static long PackCamera(float yaw, float pitch)
        {
            long y = (long)(yaw * 100f + 0.5f);
            if (y < 0L) y = 0L;
            if (y > 35999L) y = 35999L;
            long p = (long)((pitch + 90f) * 100f + 0.5f);
            if (p < 0L) p = 0L;
            if (p > 18000L) p = 18000L;
            return (y << 32) | p;
        }

        /// <summary>
        /// Unpack a value produced by <see cref="PackCamera"/>. Returns false (with
        /// yaw=pitch=0) when the snapshot is the <see cref="CameraAbsent"/> sentinel.
        /// </summary>
        public static bool TryUnpackCamera(long packed, out float yaw, out float pitch)
        {
            long y = (packed >> 32) & 0xFFFFFFFFL;
            if (y == AbsentYawQuantum)
            {
                yaw = 0f;
                pitch = 0f;
                return false;
            }
            long p = packed & 0xFFFFFFFFL;
            yaw = y / 100f;
            pitch = p / 100f - 90f;
            return true;
        }
    }
}
