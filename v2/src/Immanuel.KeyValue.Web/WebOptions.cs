namespace Immanuel.KeyValue.Web;

/// <summary>
/// Per-IP throttling. The service is free and unauthenticated, so a cap is the only thing
/// standing between one badly behaved script and everyone else's latency.
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    public bool Enabled { get; set; } = true;

    /// <summary>Requests allowed per IP address per minute.</summary>
    public int PermitsPerMinute { get; set; } = 300;

    /// <summary>How many over-limit requests to hold rather than reject outright.</summary>
    public int QueueLimit { get; set; } = 0;
}

/// <summary>
/// How much to believe X-Forwarded-For. It decides both the IP recorded against a write and
/// the partition key the rate limiter counts against, so trusting it from the open internet
/// lets anyone forge either one.
/// </summary>
public sealed class ProxyOptions
{
    public const string SectionName = "Proxy";

    /// <summary>Read X-Forwarded-For / X-Forwarded-Proto at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Accept forwarded headers from any caller. Only safe when nothing can reach the app
    /// except your reverse proxy - otherwise clients can spoof their address and slip the
    /// rate limiter. Prefer listing the proxy in <see cref="KnownProxies"/>.
    /// </summary>
    public bool TrustAllProxies { get; set; }

    /// <summary>IP addresses of reverse proxies whose forwarded headers are trusted.</summary>
    public string[] KnownProxies { get; set; } = [];
}
