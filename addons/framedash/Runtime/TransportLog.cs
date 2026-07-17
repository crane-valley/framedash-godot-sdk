namespace Framedash
{
	/// <summary>
	/// Pure formatting helpers for transport log lines, extracted so the message
	/// shape is unit-testable without the Godot engine.
	/// </summary>
	public static class TransportLog
	{
		/// <summary>
		/// Format the opt-in verbose success line (F25): a positive, client-side
		/// confirmation of delivery for first-time integrators, carrying the event
		/// count and HTTP status. Mirrors the UE5 SDK's success-log spirit.
		/// </summary>
		public static string FormatFlushSuccess(int eventCount, long statusCode)
			=> $"[Framedash] Flushed {eventCount} {(eventCount == 1 ? "event" : "events")} (HTTP {statusCode}).";
	}
}
