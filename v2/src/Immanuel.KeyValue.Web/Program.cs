using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Immanuel.KeyValue.Core;
using Immanuel.KeyValue.Web;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

var rateLimit = builder.Configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();
var proxy = builder.Configuration.GetSection(ProxyOptions.SectionName).Get<ProxyOptions>() ?? new ProxyOptions();

builder.Services.AddKeyValueStore(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddProblemDetails();

// The whole point of the service is being called from JavaScript on other people's pages,
// so every origin is allowed - the same as v1's OWIN CorsOptions.AllowAll.
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

if (proxy.Enabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        if (proxy.TrustAllProxies)
        {
            // Anything may set X-Forwarded-For. Only correct when the app is unreachable
            // except through your proxy.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
        }
        else
        {
            foreach (var address in proxy.KnownProxies)
            {
                if (IPAddress.TryParse(address, out var parsed)) options.KnownProxies.Add(parsed);
            }
        }
    });
}

if (rateLimit.Enabled)
{
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        // One window per client IP, so a single noisy caller cannot spend everyone's budget.
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = rateLimit.PermitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = rateLimit.QueueLimit,
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                }));

        options.OnRejected = async (context, token) =>
        {
            context.HttpContext.Response.Headers.RetryAfter = "60";
            await context.HttpContext.Response.WriteAsync(
                "Rate limit exceeded. Try again in a minute.", token);
        };
    });
}

var app = builder.Build();

// Must run before anything reads the client IP.
if (proxy.Enabled) app.UseForwardedHeaders();

app.UseExceptionHandler();
app.UseStatusCodePages();

// No UseHttpsRedirection: TLS is terminated at the reverse proxy in front of this app, and
// redirecting again here is the classic way to end up in a loop. Terminate TLS at the edge.

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors();
if (rateLimit.Enabled) app.UseRateLimiter();

app.MapControllers();
app.MapOpenApi();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    utc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
}));

app.Run();

/// <summary>Exposed so the integration tests can spin the app up with WebApplicationFactory.</summary>
public partial class Program;
