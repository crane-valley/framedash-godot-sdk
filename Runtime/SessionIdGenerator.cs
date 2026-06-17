using System;

namespace Framedash
{
    /// <summary>
    /// Engine-independent UUIDv7 (RFC 9562) generator with a Xoshiro256++
    /// PRNG. Mirrors the UE5 SDK's Framedash::PackUuidV7 / FXoshiro256pp
    /// (sdks/ue5/.../FramedashUuid.{h,cpp}) so session IDs share a single
    /// algorithm across SDKs. The custom path is kept because Godot's
    /// .NET 8 runtime predates .NET 9's Guid.CreateVersion7, and a shared
    /// algorithm keeps session IDs identical across the SDKs.
    ///
    /// Telemetry / correlation IDs only. Xoshiro256++ is statistically
    /// strong but not a CSPRNG, so the generated IDs MUST NOT be used as
    /// bearer tokens, authentication secrets, or anything else that
    /// relies on unguessability. Use System.Security.Cryptography for
    /// those.
    ///
    /// Pure C# (no engine references) so it compiles into the
    /// standalone NUnit harness in sdks/godot/Tests/ without a Godot
    /// install.
    /// </summary>
    public static class SessionIdGenerator
    {
        [ThreadStatic] private static Xoshiro256PlusPlus s_rng;

        /// <summary>
        /// Generate a fresh UUIDv7 string in canonical 8-4-4-4-12 lower-hex
        /// form. Safe to call from any thread; each thread lazily seeds
        /// its own ThreadStatic Xoshiro instance.
        /// </summary>
        public static string NewSessionIdV7()
        {
            ulong unixTsMs = ClampNonNegativeUnixMs(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            Xoshiro256PlusPlus rng = s_rng;
            if (rng == null)
            {
                rng = NewSeededRng();
                s_rng = rng;
            }

            // Capture Next() into named locals: argument-evaluation order
            // is well-defined left-to-right in C#, but pinning the order
            // here keeps captured logs reproducible against a fixed seed
            // and matches the UE5 implementation comment exactly.
            ulong r1 = rng.Next();
            ulong r2 = rng.Next();

            return PackUuidV7(unixTsMs, r1, r2).ToGuid().ToString("D");
        }

        /// <summary>
        /// Clamp a signed Unix-millisecond timestamp to a non-negative
        /// ulong. ToUnixTimeMilliseconds returns a signed long; a
        /// negative value would mean the system clock is set before the
        /// Unix epoch (vanishingly rare on a real device). Returning
        /// 0 instead of casting silently lets the resulting v7 ID still
        /// parse with an all-zero timestamp prefix until the clock
        /// recovers. Throwing here would violate the SDK's
        /// "NEVER throw" hard rule. Internal so the standalone NUnit
        /// harness can verify the clamp without forcing a pre-epoch
        /// system clock.
        /// </summary>
        internal static ulong ClampNonNegativeUnixMs(long unixMsSigned)
        {
            return unixMsSigned < 0 ? 0UL : (ulong)unixMsSigned;
        }

        private static Xoshiro256PlusPlus NewSeededRng()
        {
            // Seed the four Xoshiro lanes from two Guid.NewGuid() values
            // (~244 bits of effective entropy, since each v4 GUID fixes 6
            // bits for version + variant). Modern .NET implementations of
            // Guid.NewGuid draw from the OS RNG, but the exact guarantee
            // varies by runtime, so this is a non-cryptographic seed path
            // for correlation/session tracking only -- not for security
            // tokens. An all-zero seed yields an all-zero Xoshiro stream,
            // but Guid.NewGuid is overwhelmingly unlikely to produce 16
            // zero bytes, so the explicit zero-guard would be dead code.
            byte[] b1 = Guid.NewGuid().ToByteArray();
            byte[] b2 = Guid.NewGuid().ToByteArray();
            var rng = new Xoshiro256PlusPlus();
            rng.Seed(
                ReadULongLE(b1, 0),
                ReadULongLE(b1, 8),
                ReadULongLE(b2, 0),
                ReadULongLE(b2, 8));
            return rng;
        }

        private static ulong ReadULongLE(byte[] b, int offset)
        {
            return ((ulong)b[offset])
                | ((ulong)b[offset + 1] << 8)
                | ((ulong)b[offset + 2] << 16)
                | ((ulong)b[offset + 3] << 24)
                | ((ulong)b[offset + 4] << 32)
                | ((ulong)b[offset + 5] << 40)
                | ((ulong)b[offset + 6] << 48)
                | ((ulong)b[offset + 7] << 56);
        }

        /// <summary>
        /// Microsoft Guid 4x32 layout that, when stringified via
        /// ToString("D"), produces the canonical 8-4-4-4-12 hex defined by
        /// RFC 9562. Field naming matches the UE5 FUuidFields struct.
        /// </summary>
        public readonly struct UuidV7Fields
        {
            public readonly uint A;
            public readonly uint B;
            public readonly uint C;
            public readonly uint D;

            public UuidV7Fields(uint a, uint b, uint c, uint d)
            {
                A = a;
                B = b;
                C = c;
                D = d;
            }

            public Guid ToGuid()
            {
                unchecked
                {
                    return new Guid(
                        (int)A,
                        (short)(B >> 16),
                        (short)(B & 0xFFFFu),
                        (byte)((C >> 24) & 0xFFu),
                        (byte)((C >> 16) & 0xFFu),
                        (byte)((C >> 8) & 0xFFu),
                        (byte)(C & 0xFFu),
                        (byte)((D >> 24) & 0xFFu),
                        (byte)((D >> 16) & 0xFFu),
                        (byte)((D >> 8) & 0xFFu),
                        (byte)(D & 0xFFu));
                }
            }
        }

        /// <summary>
        /// Pack a UUIDv7 (RFC 9562) from a millisecond timestamp and 128
        /// bits of random material. Bit layout (MSB first):
        ///   bits   0..47  unix_ts_ms (input is truncated to 48 bits)
        ///   bits  48..51  ver = 0x7
        ///   bits  52..63  rand_a (12 bits, taken from R1[0..11])
        ///   bits  64..65  var = 0b10
        ///   bits  66..127 rand_b (62 bits)
        /// Of R1 the low 26 bits are consumed (12 for rand_a, 14 for the
        /// high half of rand_b). Of R2 the low 32 bits and bits 32..47
        /// fill the rest of rand_b. Remaining input bits are ignored.
        /// </summary>
        public static UuidV7Fields PackUuidV7(ulong unixTsMs, ulong r1, ulong r2)
        {
            unixTsMs &= 0x0000FFFFFFFFFFFFUL;

            uint randA   = (uint)(r1 & 0x0FFFu);
            uint randBHi = (uint)((r1 >> 12) & 0x3FFFu);
            uint randBMd = (uint)((r2 >> 32) & 0xFFFFu);
            uint randBLo = (uint)(r2 & 0xFFFFFFFFu);

            uint a = (uint)(unixTsMs >> 16);
            uint b = ((uint)(unixTsMs & 0xFFFFu) << 16) | (0x7000u | randA);
            uint c = ((0x8000u | randBHi) << 16) | randBMd;
            uint d = randBLo;
            return new UuidV7Fields(a, b, c, d);
        }
    }

    /// <summary>
    /// Xoshiro256++ PRNG (Blackman/Vigna). Seeded externally; thread safety
    /// is the caller's responsibility (SessionIdGenerator uses
    /// ThreadStatic instances). An all-zero seed produces an all-zero
    /// stream, so callers must seed with non-trivial entropy before
    /// calling Next().
    /// </summary>
    public sealed class Xoshiro256PlusPlus
    {
        private ulong _s0;
        private ulong _s1;
        private ulong _s2;
        private ulong _s3;

        public void Seed(ulong s0, ulong s1, ulong s2, ulong s3)
        {
            _s0 = s0;
            _s1 = s1;
            _s2 = s2;
            _s3 = s3;
        }

        public ulong Next()
        {
            // Xoshiro256++ relies on 64-bit wraparound addition, which a
            // checked compilation context would convert to
            // OverflowException. Pin the unchecked semantics here so a
            // downstream project building this source under /checked
            // (or via <CheckForOverflowUnderflow>) still gets correct
            // PRNG output.
            unchecked
            {
                ulong result = Rotl(_s0 + _s3, 23) + _s0;
                ulong t = _s1 << 17;

                _s2 ^= _s0;
                _s3 ^= _s1;
                _s1 ^= _s2;
                _s0 ^= _s3;

                _s2 ^= t;
                _s3 = Rotl(_s3, 45);

                return result;
            }
        }

        private static ulong Rotl(ulong x, int k)
        {
            return (x << k) | (x >> (64 - k));
        }
    }
}
