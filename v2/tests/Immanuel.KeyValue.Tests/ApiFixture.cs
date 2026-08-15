using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Immanuel.KeyValue.Tests;

/// <summary>
/// Boots the real web app against a throwaway data directory, with rate limiting off so a
/// test run cannot trip it.
/// </summary>
public sealed class ApiFixture : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "kv-api-tests", Guid.NewGuid().ToString("n"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("KeyValue:DataDirectory", _dataDirectory);
        builder.UseSetting("RateLimit:Enabled", "false");

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["KeyValue:DataDirectory"] = _dataDirectory,
                ["RateLimit:Enabled"] = "false",
            }));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        try
        {
            if (Directory.Exists(_dataDirectory)) Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
