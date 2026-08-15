using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Immanuel.KeyValue.Tests;

/// <summary>
/// Boots the real web app against a throwaway data directory, with rate limiting off so a
/// test run cannot trip it.
///
/// No SMTP is configured, which is exactly the fallback the account tests want: every sign-in
/// accepts <see cref="MasterOtp"/>, so a test can complete the flow without a mailbox.
/// </summary>
public class ApiFixture : WebApplicationFactory<Program>
{
    /// <summary>The code every sign-in accepts here, because no relay is configured.</summary>
    public const string MasterOtp = "246810";

    /// <summary>Where this fixture's databases and per-account folders end up.</summary>
    public string DataDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "kv-api-tests", Guid.NewGuid().ToString("n"));

    /// <summary>Whether the master code comes back in the sign-in response.</summary>
    protected virtual bool RevealMasterOtp => false;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Every setting is pinned rather than inherited: the tests run under the Development
        // environment, so an appsettings.Development.json on the machine would otherwise decide
        // how they behave.
        var settings = new Dictionary<string, string?>
        {
            ["KeyValue:DataDirectory"] = DataDirectory,
            ["RateLimit:Enabled"] = "false",
            ["Auth:Enabled"] = "true",
            ["Auth:MasterOtp"] = MasterOtp,
            ["Auth:RevealMasterOtp"] = RevealMasterOtp ? "true" : "false",
            ["Auth:Smtp:Host"] = "",
            ["Auth:OtpMaxAttempts"] = "3",
        };

        foreach (var (key, value) in settings) builder.UseSetting(key, value);

        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(settings));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (!disposing) return;

        try
        {
            if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// The same app with <c>Auth:RevealMasterOtp</c> on - the local-development setting that lets the
/// console fill the code in for you.
/// </summary>
public sealed class RevealingApiFixture : ApiFixture
{
    protected override bool RevealMasterOtp => true;
}
