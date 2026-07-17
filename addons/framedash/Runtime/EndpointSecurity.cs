#nullable enable

using System;

namespace Framedash
{
    /// <summary>
    /// Engine-independent endpoint URL security checks for telemetry transport.
    /// Kept free of engine (Godot) types so it can be unit-tested under NUnit.
    /// </summary>
    public static class EndpointSecurity
    {
        // Hoisted to a static field so ExtractRawHost does not allocate a new char[]
        // on every call (GC-pressure hygiene -- standard practice in game code even
        // though this path is init/flush-time, not per-frame).
        private static readonly char[] AuthorityDelimiters = { '/', '?', '#', '\\' };

        /// <summary>
        /// Whether it is safe to send the API key to this endpoint. HTTPS is
        /// always allowed; plain HTTP is allowed only for a canonical loopback host
        /// (localhost / 127.0.0.1 / [::1]), matching the UE5 SDK's exact textual
        /// allowlist. A substring test such as StartsWith("http://localhost") would
        /// accept hostile URLs like "http://localhost.attacker.com" or
        /// "http://localhost@evil.example" and leak the API key in cleartext to a
        /// non-loopback host.
        /// </summary>
        public static bool IsEndpointTransportSecure(string? endpointUrl)
        {
            if (string.IsNullOrEmpty(endpointUrl)) return false;
            // Reject control characters (incl. embedded NUL): a real URL never
            // contains raw control bytes, and a NUL could truncate the string in
            // the native HTTP client (Godot's HttpRequest) and open a
            // parser differential. Matches the UE5 validator.
            foreach (char c in endpointUrl)
            {
                if (c < ' ' || c == (char)0x7f) return false;
            }
            // Reject userinfo '@' and backslash. Telemetry endpoints never use
            // them, and System.Uri (WHATWG: '\' -> '/') resolves a different host
            // than the platform HTTP client (libcurl/RFC 3986 splits at the last
            // '@'), so "http://localhost\@evil.com" would pass the loopback check
            // here yet send the key in cleartext to evil.com. Refusing both closes
            // the gap (and lets the raw-host parse below assume no userinfo).
            if (endpointUrl.IndexOf('@') >= 0 || endpointUrl.IndexOf('\\') >= 0) return false;
            if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme == Uri.UriSchemeHttps) return true;
            if (uri.Scheme == Uri.UriSchemeHttp)
            {
                // Compare the RAW host text (parsed from the original string), NOT
                // uri.Host: System.Uri canonicalizes non-canonical loopback spellings
                // -- 127.1, 0x7f000001, 2130706433, 0177.0.0.1 all normalize to
                // "127.0.0.1", and "[0:0:0:0:0:0:0:1]" to "[::1]" -- so comparing
                // uri.Host would silently grant them the cleartext exemption that the
                // UE5 SDK (exact textual match) rejects. Those all genuinely resolve to
                // loopback so it is not a key leak, but the goal is an exact, audited
                // allowlist that does not widen with System.Uri behavior. The upfront
                // control-char / '@' / '\' rejection plus the successful TryCreate make
                // the raw parse safe (no userinfo, well-formed authority).
                return IsCanonicalLoopback(ExtractRawHost(endpointUrl));
            }
            return false;
        }

        /// <summary>
        /// Extract the lowercased host from a URL using the original text, dropping
        /// the scheme, userinfo, port, and path/query/fragment. IPv6 literals keep
        /// their brackets (e.g. "[::1]"). Returns an empty string for a malformed
        /// authority (e.g. trailing text after an IPv6 "]"). Mirrors the UE5
        /// ExtractUrlHost step for step so the two SDKs accept exactly the same set,
        /// and is correct on its own (does not rely on the caller's '@'/'\' rejection).
        /// </summary>
        private static string ExtractRawHost(string url)
        {
            int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            string remainder = schemeEnd < 0 ? url : url.Substring(schemeEnd + 3);

            // The authority ends at the first '/', '?', '#', or '\'. The backslash
            // matters because WHATWG treats '\' as '/' for special schemes, so it must
            // bound the host (mirrors UE5; the caller also rejects '\' outright).
            int authorityEnd = remainder.IndexOfAny(AuthorityDelimiters);
            string authority = authorityEnd < 0 ? remainder : remainder.Substring(0, authorityEnd);

            // Drop any userinfo ("user:pass@host" -> "host") using the LAST '@' so a
            // userinfo segment that itself contains '@' cannot smuggle in a fake host.
            // Defense in depth / UE5 parity: the caller already rejects '@', but
            // stripping here keeps ExtractRawHost correct if that check is relaxed.
            int at = authority.LastIndexOf('@');
            if (at >= 0) authority = authority.Substring(at + 1);

            if (authority.Length > 0 && authority[0] == '[')
            {
                int close = authority.IndexOf(']');
                if (close < 0) return string.Empty; // unterminated bracket -> malformed
                // Anything between ']' and the optional ":port" is malformed -- fail
                // closed so "[::1].evil" cannot pose as the loopback literal.
                if (close + 1 < authority.Length && authority[close + 1] != ':') return string.Empty;
                return ToLowerAscii(authority.Substring(0, close + 1));
            }

            int colon = authority.IndexOf(':');
            if (colon >= 0) authority = authority.Substring(0, colon);
            return ToLowerAscii(authority);
        }

        private static bool IsCanonicalLoopback(string host)
        {
            return host == "localhost" || host == "127.0.0.1" || host == "[::1]";
        }

        // ASCII-only lowercase, matching the UE5 validator. Deliberately not
        // ToLowerInvariant on the whole string elsewhere: only host equality needs
        // folding, and the allowlist is ASCII.
        private static string ToLowerAscii(string s)
        {
            char[] buf = s.ToCharArray();
            for (int i = 0; i < buf.Length; i++)
            {
                char c = buf[i];
                if (c >= 'A' && c <= 'Z') buf[i] = (char)(c - 'A' + 'a');
            }
            return new string(buf);
        }
    }
}
