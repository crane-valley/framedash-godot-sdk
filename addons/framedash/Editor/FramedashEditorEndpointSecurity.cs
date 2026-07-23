#if TOOLS
#nullable disable

using System;

namespace Framedash.Editor.Logic
{
    public static class FramedashEditorEndpointSecurity
    {
        private readonly struct UrlOrigin
        {
            public UrlOrigin(string scheme, string host, string port)
            {
                Scheme = scheme;
                Host = host;
                Port = port;
            }

            public string Scheme { get; }
            public string Host { get; }
            public string Port { get; }
        }

        public static bool IsCrossOriginRedirect(string configuredUrl, string effectiveUrl)
        {
            if (string.IsNullOrEmpty(effectiveUrl))
            {
                return false;
            }
            if (HasUnsafeUrlCharacter(configuredUrl) || HasUnsafeUrlCharacter(effectiveUrl))
            {
                return true;
            }

            // The runtime path fails open on an unparseable origin to avoid losing
            // collected telemetry (#1080). Editor reads are retriable with another
            // Fetch click, so there is no telemetry-loss cost to weigh against failing closed.
            if (!TryParseOrigin(configuredUrl, out UrlOrigin configuredOrigin)
                || !TryParseOrigin(effectiveUrl, out UrlOrigin effectiveOrigin))
            {
                return true;
            }
            return configuredOrigin.Scheme != effectiveOrigin.Scheme
                || configuredOrigin.Host != effectiveOrigin.Host
                || configuredOrigin.Port != effectiveOrigin.Port;
        }

        private static bool HasUnsafeUrlCharacter(string url)
        {
            if (url == null)
            {
                return true;
            }
            for (int i = 0; i < url.Length; i++)
            {
                char character = url[i];
                if (character < ' ' || character == (char)0x7f)
                {
                    return true;
                }
            }
            return url.IndexOf('@') >= 0 || url.IndexOf('\\') >= 0;
        }

        private static bool TryParseOrigin(string url, out UrlOrigin origin)
        {
            origin = new UrlOrigin();
            if (string.IsNullOrEmpty(url))
            {
                return false;
            }

            int schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeEnd <= 0)
            {
                return false;
            }
            string scheme = ToLowerAscii(url.Substring(0, schemeEnd));
            if (scheme != "http" && scheme != "https")
            {
                return false;
            }

            string remainder = url.Substring(schemeEnd + 3);
            int authorityEnd = IndexOfAuthorityEnd(remainder);
            string authority = authorityEnd < 0 ? remainder : remainder.Substring(0, authorityEnd);
            if (authority.Length == 0)
            {
                return false;
            }

            string host;
            string port = string.Empty;
            if (authority[0] == '[')
            {
                int close = authority.IndexOf(']');
                if (close < 0)
                {
                    return false;
                }
                host = authority.Substring(0, close + 1);
                string afterClose = authority.Substring(close + 1);
                if (afterClose.Length > 0)
                {
                    if (afterClose[0] != ':')
                    {
                        return false;
                    }
                    port = afterClose.Substring(1);
                }
            }
            else
            {
                int colon = authority.IndexOf(':');
                if (colon < 0)
                {
                    host = authority;
                }
                else
                {
                    // Keeping malformed raw port text makes it unequal to a valid origin
                    // without introducing a parser-dependent fail-open path.
                    host = authority.Substring(0, colon);
                    port = authority.Substring(colon + 1);
                }
            }
            if (host.Length == 0)
            {
                return false;
            }
            if (port.Length == 0)
            {
                port = scheme == "https" ? "443" : "80";
            }
            origin = new UrlOrigin(scheme, ToLowerAscii(host), port);
            return true;
        }

        private static int IndexOfAuthorityEnd(string value)
        {
            int result = -1;
            for (int i = 0; i < value.Length; i++)
            {
                char character = value[i];
                if (character == '/' || character == '?' || character == '#')
                {
                    result = i;
                    break;
                }
            }
            return result;
        }

        private static string ToLowerAscii(string value)
        {
            char[] characters = value.ToCharArray();
            for (int i = 0; i < characters.Length; i++)
            {
                char character = characters[i];
                if (character >= 'A' && character <= 'Z')
                {
                    characters[i] = (char)(character - 'A' + 'a');
                }
            }
            return new string(characters);
        }
    }
}
#endif
