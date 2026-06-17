namespace Framedash
{
    /// <summary>
    /// Manages session and player identity for telemetry events.
    /// A new session ID is generated each time the game starts.
    /// Player ID is developer-supplied; defaults to empty (anonymous).
    /// </summary>
    public sealed class SessionManager
    {
        public string SessionId { get; }
        public string PlayerId { get; private set; }

        // Matches packages/ingest-core/src/config.ts MAX_PLAYER_ID_LEN. An over-limit
        // player_id is rejected by ingest validation, which drops the whole batch, so the
        // SDK truncates it (after trimming whitespace) before storing.
        private const int MaxPlayerIdLen = 128;

        private static string NormalizePlayerId(string playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return "";
            // Reuse FieldClamp.Truncate so player_id shares the surrogate-pair-safe
            // truncation used for the other string fields.
            return FieldClamp.Truncate(playerId.Trim(), MaxPlayerIdLen);
        }

        public SessionManager(string playerId = null)
        {
            SessionId = SessionIdGenerator.NewSessionIdV7();
            PlayerId = NormalizePlayerId(playerId);
        }

        /// <summary>
        /// Update the player ID at runtime (e.g. after login).
        /// </summary>
        public void SetPlayerId(string playerId)
        {
            PlayerId = NormalizePlayerId(playerId);
        }
    }
}
