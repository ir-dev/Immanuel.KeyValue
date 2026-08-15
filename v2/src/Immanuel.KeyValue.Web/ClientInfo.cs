namespace Immanuel.KeyValue.Web;

/// <summary>
/// Pulls the caller's address and user agent off the request. v1 dug these out of
/// MS_HttpContext / RemoteEndpointMessageProperty because Web API could be self-hosted;
/// ASP.NET Core just has them, and the forwarded-headers middleware has already replaced
/// RemoteIpAddress with the real client address when a trusted proxy is in front.
/// </summary>
internal static class ClientInfo
{
    /// <summary>v1 stored the agent in a varchar(128), so keep the same ceiling.</summary>
    private const int MaxAgentLength = 128;

    public static string? IpAddress(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString();

    public static string? UserAgent(HttpContext context)
    {
        var agent = context.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrWhiteSpace(agent)) return null;

        return agent.Length <= MaxAgentLength ? agent : agent[..MaxAgentLength];
    }
}
