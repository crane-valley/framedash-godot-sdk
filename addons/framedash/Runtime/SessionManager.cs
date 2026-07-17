#nullable enable

using System.Collections.Generic;

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

        // Active automated (CI) session set by TelemetrySDK.BeginAutomatedSession: the
        // build_id override and the ci.* attributes (ci.branch / ci.commit / ci.scenario)
        // published as ONE immutable snapshot so the stamping path reads both from a single
        // point. A background Track() must never observe a new candidate build_id paired with
        // the previous or cleared ci.* labels (or vice versa), which a torn read across two
        // separate fields would allow. volatile: written on the main thread (Set/Clear), read
        // once by the possibly-background Track() stamping path. null = no session active.
        private volatile AutomatedSession? _automated;

        // Immutable snapshot of an automated session. BuildId is null when the session does
        // not override the build_id (the stamp then uses the configured build_id) and is
        // stored raw -- it is truncated to the build_id cap at stamp time (FieldClamp.Truncate
        // in ResolveSessionStamp's caller). Attributes is null when the session set no ci.*
        // tags and is already clamped to the ingest attribute caps when non-null.
        private sealed class AutomatedSession
        {
            public readonly string? BuildId;
            public readonly List<StringPair>? Attributes;
            public AutomatedSession(string? buildId, List<StringPair>? attributes)
            {
                BuildId = buildId;
                Attributes = attributes;
            }
        }

        // The build_id and merged attribute list resolved for one event from a single session
        // snapshot, so the pair is always mutually consistent.
        public readonly struct SessionStamp
        {
            public readonly string BuildId;
            public readonly List<StringPair>? Attributes;
            public SessionStamp(string buildId, List<StringPair>? attributes)
            {
                BuildId = buildId;
                Attributes = attributes;
            }
        }

        private static string NormalizePlayerId(string? playerId)
        {
            if (string.IsNullOrWhiteSpace(playerId)) return "";
            // Reuse FieldClamp.Truncate so player_id shares the surrogate-pair-safe
            // truncation used for the other string fields.
            return FieldClamp.Truncate(playerId.Trim(), MaxPlayerIdLen);
        }

        public SessionManager(string? playerId = null)
        {
            SessionId = SessionIdGenerator.NewSessionIdV7();
            PlayerId = NormalizePlayerId(playerId);
        }

        /// <summary>
        /// Update the player ID at runtime (e.g. after login).
        /// </summary>
        public void SetPlayerId(string? playerId)
        {
            PlayerId = NormalizePlayerId(playerId);
        }

        /// <summary>
        /// Install the automated-session build_id override and ci.* attributes as one atomic
        /// snapshot (called by BeginAutomatedSession). <paramref name="buildId"/> is null to
        /// leave the configured build_id in place; the dictionary is clamped to the ingest
        /// attribute caps, and a null/empty dictionary sets no tags. With neither a build_id
        /// nor any attributes the session is cleared.
        /// </summary>
        public void SetAutomatedSession(string? buildId, Dictionary<string, string>? attributes)
        {
            var clamped = FieldClamp.ClampAttributes(attributes);
            var attrs = (clamped != null && clamped.Count > 0) ? clamped : null;
            _automated = (buildId == null && attrs == null) ? null : new AutomatedSession(buildId, attrs);
        }

        /// <summary>
        /// Replace the session-level attributes with no build_id override. Equivalent to
        /// <c>SetAutomatedSession(null, attributes)</c>; a null or empty dictionary clears them.
        /// </summary>
        public void SetSessionAttributes(Dictionary<string, string>? attributes)
            => SetAutomatedSession(null, attributes);

        /// <summary>Clear the automated session (ends the build_id override and ci.* tagging).</summary>
        public void ClearSessionAttributes()
        {
            _automated = null;
        }

        /// <summary>True when an automated session has set session-level attributes.</summary>
        public bool HasSessionAttributes => _automated?.Attributes != null;

        /// <summary>
        /// Resolve the active automated session against one event from a SINGLE snapshot read:
        /// returns the build_id to stamp (the session override, or <paramref name="fallbackBuildId"/>
        /// when the session set none) and the merged attribute list. Because both come from one
        /// read of the session reference, the pair is always mutually consistent even if
        /// Begin/EndAutomatedSession runs on the main thread while this executes on a
        /// background Track() thread -- a post-End event can never carry the candidate build_id
        /// with cleared tags, nor a new build_id with stale tags.
        /// </summary>
        public SessionStamp ResolveSessionStamp(string fallbackBuildId, List<StringPair>? eventAttributes)
        {
            var s = _automated; // single volatile read -> consistent (BuildId, Attributes)
            if (s == null) return new SessionStamp(fallbackBuildId, eventAttributes);
            return new SessionStamp(s.BuildId ?? fallbackBuildId, MergeAttributes(s.Attributes, eventAttributes));
        }

        /// <summary>
        /// Merge the active session-level attributes with a per-event attribute list (reads
        /// the session snapshot once). Returns <paramref name="eventAttributes"/> unchanged
        /// when no session attributes are set; returns the shared session list directly (no
        /// allocation) when there are no per-event ones, which keeps the periodic heartbeat
        /// allocation-free. The result is capped at <see cref="FieldClamp.MaxAttributes"/>.
        /// </summary>
        public List<StringPair>? MergeWithSessionAttributes(List<StringPair>? eventAttributes)
        {
            // _automated is read once here; its Attributes reference is then immutable.
            return MergeAttributes(_automated?.Attributes, eventAttributes);
        }

        // Pure merge: session entries FIRST (so the CI metadata survives if the combined set
        // exceeds the cap), per-event entries overriding a session entry on a key collision,
        // capped at FieldClamp.MaxAttributes. sessionAttrs is the caller's already-snapshotted
        // reference, so this never re-reads the volatile field.
        private static List<StringPair>? MergeAttributes(List<StringPair>? sessionAttrs, List<StringPair>? eventAttributes)
        {
            if (sessionAttrs == null) return eventAttributes;
            if (eventAttributes == null || eventAttributes.Count == 0) return sessionAttrs;

            var combined = new List<StringPair>(sessionAttrs.Count + eventAttributes.Count);
            combined.AddRange(sessionAttrs);
            foreach (var pair in eventAttributes)
            {
                // Per-event keys are unique in the source Dictionary, but key truncation
                // (FieldClamp.ClampAttributes) can collapse two distinct keys onto the same
                // value, so do not rely on per-event uniqueness for correctness. We only
                // resolve collisions against the leading session block; a rare post-
                // truncation duplicate among per-event keys is passed through as-is, exactly
                // as it would be without a session. Override the session value in place, or
                // append a genuinely new per-event key.
                bool overridden = false;
                for (int i = 0; i < sessionAttrs.Count; i++)
                {
                    if (combined[i].Key == pair.Key)
                    {
                        combined[i] = pair;
                        overridden = true;
                        break;
                    }
                }
                if (!overridden) combined.Add(pair);
            }
            if (combined.Count > FieldClamp.MaxAttributes)
            {
                combined.RemoveRange(FieldClamp.MaxAttributes, combined.Count - FieldClamp.MaxAttributes);
            }
            return combined;
        }
    }
}
