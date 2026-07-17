using System.Threading;

namespace Framedash
{
	/// <summary>
	/// Single-flight flush guard with a re-init generation, extracted from
	/// <c>TelemetrySDK</c> so the concurrency semantics are unit-testable without the
	/// Godot engine.
	///
	/// Only one flush may be in flight at a time (the transport owns a single
	/// HttpRequest node), so a new flush is refused while one is running. The bounded
	/// transport wall-time (see <see cref="RetryPolicy.WorstCaseTotalSeconds"/>)
	/// guarantees the gate is released promptly, so a wedged endpoint cannot starve
	/// later flushes (heartbeats, session_end, the shutdown flush) for more than the
	/// bounded window.
	///
	/// The generation defends the re-init path (#1044): a stale flush completion from
	/// a prior session must never clear the guard the new session now owns.
	///
	/// The generation and the in-flight flag are packed into a SINGLE 64-bit state
	/// word and mutated only via Interlocked.CompareExchange, so every operation is
	/// atomic. In particular <see cref="ReleaseIfGeneration"/> cannot be preempted
	/// between "check generation" and "clear flag": if a concurrent <see cref="Reset"/>
	/// (and a new flush's <see cref="TryBegin"/>) slips in, the CAS fails and the stale
	/// release re-evaluates against the new generation and becomes a no-op -- it can
	/// never clear the new session's active gate.
	/// </summary>
	public sealed class FlushGate
	{
		// High 32 bits: generation. Low 32 bits: in-flight flag (0 = idle, 1 = flushing).
		private long _state;

		private static int GenerationOf(long state) => (int)(state >> 32);
		private static int FlagOf(long state) => (int)(state & 0xFFFFFFFFL);
		private static long Pack(int generation, int flag) => ((long)generation << 32) | (uint)flag;

		/// <summary>The current flush generation (incremented on each re-init).</summary>
		public int Generation => GenerationOf(Interlocked.Read(ref _state));

		/// <summary>Whether a flush is currently in flight.</summary>
		public bool IsFlushing => FlagOf(Interlocked.Read(ref _state)) == 1;

		/// <summary>
		/// Try to acquire the gate. Returns true only for the caller that wins the
		/// single-flight race; a concurrent caller gets false and must skip its flush.
		/// </summary>
		public bool TryBegin()
		{
			while (true)
			{
				long state = Interlocked.Read(ref _state);
				if (FlagOf(state) != 0) return false;
				long next = Pack(GenerationOf(state), 1);
				if (Interlocked.CompareExchange(ref _state, next, state) == state) return true;
			}
		}

		/// <summary>
		/// Unconditionally release the gate (keeping the current generation). Used on
		/// the synchronous early-return / error paths inside the same Flush() call.
		/// </summary>
		public void Release()
		{
			while (true)
			{
				long state = Interlocked.Read(ref _state);
				if (FlagOf(state) == 0) return;
				long next = Pack(GenerationOf(state), 0);
				if (Interlocked.CompareExchange(ref _state, next, state) == state) return;
			}
		}

		/// <summary>
		/// Release the gate only if the supplied generation still matches -- used by
		/// the async completion path so a stale flush from a prior session cannot clear
		/// the guard the new session owns (#1044 flush-generation guard). Atomic: a
		/// concurrent re-init makes this a no-op rather than a premature release.
		/// </summary>
		public void ReleaseIfGeneration(int generation)
		{
			while (true)
			{
				long state = Interlocked.Read(ref _state);
				if (GenerationOf(state) != generation) return; // stale: new session owns the gate
				if (FlagOf(state) == 0) return;                // already released
				long next = Pack(generation, 0);
				if (Interlocked.CompareExchange(ref _state, next, state) == state) return;
				// CAS failed: state changed concurrently -- re-read and re-evaluate.
			}
		}

		/// <summary>
		/// Re-init: clear any leftover in-flight flag and start a new generation so a
		/// still-running prior flush cannot release the new session's guard. Returns
		/// the new generation.
		/// </summary>
		public int Reset()
		{
			while (true)
			{
				long state = Interlocked.Read(ref _state);
				int newGeneration = GenerationOf(state) + 1;
				long next = Pack(newGeneration, 0);
				if (Interlocked.CompareExchange(ref _state, next, state) == state) return newGeneration;
			}
		}
	}
}
