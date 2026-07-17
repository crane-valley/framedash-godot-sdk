#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;

namespace Framedash
{
    /// <summary>
    /// Address family for a delivery attempt. Kept engine-independent (does not
    /// reference Godot's IP.Type) so the ordering/URL-rewrite logic is NUnit-testable.
    /// </summary>
    public enum IpFamily
    {
        IPv4,
        IPv6,
    }

    /// <summary>
    /// Immutable delivery plan produced by <see cref="EndpointAddressPlanner"/>: the
    /// ordered IP-literal URLs to attempt (IPv4 preferred, IPv6 fallback) plus the
    /// Host header and TLS common-name override that make an IP-literal connect still
    /// route and validate as the original FQDN.
    ///
    /// A "passthrough" plan (<see cref="IsPassthrough"/>) carries the single original
    /// URL with no host rewrite -- used for loopback / IP-literal / non-HTTPS endpoints,
    /// or when DNS resolution yielded nothing, so the engine resolves normally.
    /// </summary>
    public sealed class EndpointAddressPlan
    {
        /// <summary>
        /// IP-literal URLs to attempt in order. IPv4 first (broken-IPv6 networks -- a
        /// global AAAA via Router Advertisement but no working IPv6 route -- are the
        /// dominant real-world failure and Cloudflare IPv4 anycast is near-universally
        /// reachable), IPv6 second (so an IPv6-only network still delivers when the IPv4
        /// attempt fails). For a passthrough plan this is the single original URL.
        /// </summary>
        public IReadOnlyList<string> AttemptUrls { get; }

        /// <summary>
        /// Explicit Host header value ("host" or "host:port"). Empty for a passthrough
        /// plan. When set, the transport sends it so Godot's HTTPClient does not inject
        /// its own Host = the IP literal (which Cloudflare Worker route-matching needs to
        /// be the hostname).
        /// </summary>
        public string HostHeader { get; }

        /// <summary>
        /// TLS common-name override (the FQDN). Empty for a passthrough plan. Drives
        /// mbedtls_ssl_set_hostname, which sets BOTH the SNI extension and the
        /// certificate-verification name, so a connect to an IP literal still negotiates
        /// and validates against the real hostname.
        /// </summary>
        public string CommonName { get; }

        /// <summary>
        /// True when no host rewrite applies: use the original URL and let the engine
        /// resolve/validate normally (loopback, IP-literal, or non-HTTPS endpoints, or a
        /// total resolution failure). Derived from an empty <see cref="CommonName"/>.
        /// </summary>
        public bool IsPassthrough => CommonName.Length == 0;

        public EndpointAddressPlan(IReadOnlyList<string> attemptUrls, string hostHeader, string commonName)
        {
            // Store a defensive read-only snapshot so the documented immutability holds
            // even if the caller passes a mutable List/array and later mutates it (a
            // consumer cannot downcast AttemptUrls back to a mutable type).
            AttemptUrls = Array.AsReadOnly(attemptUrls.ToArray());
            HostHeader = hostHeader;
            CommonName = commonName;
        }
    }

    /// <summary>
    /// Pure, engine-independent planner for the prefer-IPv4-with-IPv6-fallback ingest
    /// connect. It never calls engine APIs (the caller performs the actual DNS
    /// resolution via Godot's IP.ResolveHostname and passes the results in), so the
    /// address-family ordering and URL-rewrite logic can be unit-tested under NUnit.
    /// </summary>
    public static class EndpointAddressPlanner
    {
        /// <summary>
        /// Family attempt order: IPv4 first, IPv6 second. See
        /// <see cref="EndpointAddressPlan.AttemptUrls"/> for the rationale.
        /// </summary>
        public static readonly IReadOnlyList<IpFamily> PreferenceOrder =
            Array.AsReadOnly(new[] { IpFamily.IPv4, IpFamily.IPv6 });

        /// <summary>
        /// Whether the forced-IP-literal path applies to this endpoint at all. Only real
        /// remote HTTPS hostnames benefit; loopback / IP-literal / non-HTTPS endpoints
        /// (plain HTTP is already loopback-only per EndpointSecurity) pass through
        /// unchanged so local dev and self-hosted-by-IP deployments keep working.
        /// </summary>
        public static bool ShouldForceAddressFamily(string? endpointUrl)
        {
            if (string.IsNullOrEmpty(endpointUrl)) return false;
            if (!Uri.TryCreate(endpointUrl, UriKind.Absolute, out var uri)) return false;
            // HTTP endpoints are loopback-only (EndpointSecurity) -- no IPv6-blackhole
            // exposure and forcing would only risk breaking local dev.
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            // Skip IP literals (already pinned to one address) and localhost.
            if (uri.HostNameType != UriHostNameType.Dns) return false;
            if (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }

        /// <summary>
        /// Build the delivery plan. <paramref name="resolvedIPv4"/> /
        /// <paramref name="resolvedIPv6"/> are the first resolved address of each family
        /// (empty when that family did not resolve). When the endpoint is passthrough or
        /// neither family resolved, returns a passthrough plan on the original URL so the
        /// engine resolves normally rather than the SDK dropping the batch.
        /// </summary>
        public static EndpointAddressPlan Build(string endpointUrl, string? resolvedIPv4, string? resolvedIPv6)
        {
            if (!ShouldForceAddressFamily(endpointUrl))
                return Passthrough(endpointUrl);

            var urls = new List<string>(2);
            foreach (var family in PreferenceOrder)
            {
                string? ip = family == IpFamily.IPv4 ? resolvedIPv4 : resolvedIPv6;
                if (!string.IsNullOrEmpty(ip))
                    urls.Add(RewriteHost(endpointUrl, ip!, family));
            }

            if (urls.Count == 0)
                return Passthrough(endpointUrl);

            var uri = new Uri(endpointUrl);
            return new EndpointAddressPlan(urls, HostHeaderValue(endpointUrl), uri.Host);
        }

        /// <summary>
        /// Next family index after a transport-level failure (status 0): a modulo
        /// TOGGLE, not advance-and-clamp. Wrapping matters on a broken-IPv6 network --
        /// after a single transient IPv4 glitch pushes selection to IPv6 (a blackhole),
        /// advance-and-clamp would wedge every remaining retry on IPv6 for the full
        /// timeout; wrapping returns to the preferred IPv4 on the next attempt. For a
        /// single-URL (passthrough) plan the modulo keeps the index at 0 (no-op).
        /// </summary>
        public static int NextFamily(int currentIndex, int attemptUrlCount)
        {
            if (attemptUrlCount <= 0) return 0;
            return (currentIndex + 1) % attemptUrlCount;
        }

        private static EndpointAddressPlan Passthrough(string endpointUrl) =>
            new EndpointAddressPlan(new[] { endpointUrl }, string.Empty, string.Empty);

        /// <summary>
        /// Rewrite the authority host of <paramref name="endpointUrl"/> with an IP
        /// literal (bracketing IPv6), preserving scheme, port, path, query, and fragment.
        /// </summary>
        public static string RewriteHost(string endpointUrl, string ipLiteral, IpFamily family)
        {
            // Defensive (public API): a null/empty IP literal has nothing to substitute,
            // so return the URL unchanged rather than emitting a malformed authority.
            // Internal callers never pass empty (Build skips a family with no address).
            if (string.IsNullOrEmpty(ipLiteral)) return endpointUrl;

            // UriBuilder rebuilds the authority correctly for custom/self-hosted
            // endpoints (preserving scheme, port, path, query, fragment, and userinfo)
            // instead of hand-concatenating, which could drop userinfo or mangle a
            // non-standard authority. IPv6 must be bracketed for UriBuilder.Host.
            var builder = new UriBuilder(endpointUrl)
            {
                Host = family == IpFamily.IPv6 ? "[" + StripBrackets(ipLiteral) + "]" : ipLiteral,
            };
            return builder.Uri.ToString();
        }

        /// <summary>
        /// The Host header value for the endpoint: "host" for a default port, otherwise
        /// "host:port" (matching how HTTP clients render the Host header).
        /// </summary>
        public static string HostHeaderValue(string endpointUrl)
        {
            var uri = new Uri(endpointUrl);
            return uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
        }

        // Godot's IP.ResolveHostname returns a bare IPv6 (no brackets); guard against a
        // caller that already bracketed so RewriteHost never emits "[[..]]".
        private static string StripBrackets(string ip)
        {
            if (ip.Length >= 2 && ip[0] == '[' && ip[ip.Length - 1] == ']')
                return ip.Substring(1, ip.Length - 2);
            return ip;
        }
    }
}
