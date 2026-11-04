namespace Framedash
{
	/// <summary>
	/// Pure formatting helpers for transport log lines, extracted so the message
	/// shape is unit-testable without the Godot engine.
	/// </summary>
	public static class TransportLog
	{
		public static string FormatFlushSuccess(int eventCount, long statusCode)
			=> $"[Framedash] Flushed {eventCount} {(eventCount == 1 ? "event" : "events")} (HTTP {statusCode}).";
	}
}
