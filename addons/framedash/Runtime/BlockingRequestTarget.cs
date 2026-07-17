#nullable enable

using System;

namespace Framedash
{
    /// <summary>
    /// Connection parameters for the synchronous shutdown-drain POST, parsed from the
    /// configured endpoint URL. The blocking drain uses Godot's low-level HttpClient
    /// (ConnectToHost + RequestRaw) rather than the HttpRequest scene-tree node, so it
    /// needs the host / port / TLS flag / request path split out explicitly instead of
    /// a single URL. Kept engine-independent (only System.Uri) so the parsing is
    /// NUnit-testable -- getting the default port and the path+query right is the part
    /// worth a test, not the engine call.
    /// </summary>
    public readonly struct BlockingRequestTarget
    {
        public string Host { get; }
        public int Port { get; }
        public bool UseTls { get; }

        /// <summary>Request path including query, always non-empty (at least "/").</summary>
        public string Path { get; }

        private BlockingRequestTarget(string host, int port, bool useTls, string path)
        {
            Host = host;
            Port = port;
            UseTls = useTls;
            Path = path;
        }

        /// <summary>
        /// Parse an absolute http/https endpoint into HttpClient connection parameters.
        /// Returns false for a relative URL or a non-http(s) scheme so the caller drops
        /// the drain rather than dialing an unsupported target (fail-safe). The host is
        /// the DNS-safe form (unbracketed for an IPv6 literal, which is what
        /// HttpClient.ConnectToHost expects). Port falls back to the scheme default
        /// (443/80) via System.Uri when the URL omits it.
        /// </summary>
        public static bool TryParse(string? endpointUrl, out BlockingRequestTarget target)
        {
            target = default;
            if (string.IsNullOrEmpty(endpointUrl)) return false;
            if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri)) return false;

            bool useTls;
            if (uri.Scheme == Uri.UriSchemeHttps) useTls = true;
            else if (uri.Scheme == Uri.UriSchemeHttp) useTls = false;
            else return false;

            // PathAndQuery is "/..." for any absolute http(s) URI, but guard an empty
            // value so RequestRaw always gets a valid request target.
            string path = string.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
            target = new BlockingRequestTarget(uri.DnsSafeHost, uri.Port, useTls, path);
            return true;
        }
    }
}
