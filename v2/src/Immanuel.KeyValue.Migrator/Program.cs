using Immanuel.KeyValue.Migrator;
using Microsoft.Extensions.Configuration;

// appsettings.json -> appsettings.{Environment}.json -> environment variables -> command line.
// The Development file holds the connection string and is git-ignored.
var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development";

var configurationBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddJsonFile($"appsettings.{environment}.json", optional: true)
    .AddEnvironmentVariables();

// KEYVALUE_SOURCE is the short spelling of ConnectionStrings:KeyValueSource, kept because it
// is easier to type in a shell than the double-underscore form.
var sourceFromEnvironment = Environment.GetEnvironmentVariable("KEYVALUE_SOURCE");
if (!string.IsNullOrWhiteSpace(sourceFromEnvironment))
{
    configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["ConnectionStrings:KeyValueSource"] = sourceFromEnvironment,
    });
}

var configuration = configurationBuilder.Build();

MigratorOptions options;

try
{
    options = MigratorOptions.Parse(args, configuration);
}
catch (MigratorOptions.HelpRequested)
{
    Console.WriteLine(MigratorOptions.HelpText);
    return 0;
}
catch (ArgumentException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine();
    Console.Error.WriteLine(MigratorOptions.HelpText);
    return 2;
}

// Ctrl+C stops between app keys rather than mid-write.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
    Console.WriteLine("\nStopping after the current app key...");
};

Console.WriteLine($"Environment  : {environment}");
Console.WriteLine($"Source       : {Describe(options.Source)}  (from {options.SourceOrigin})");
Console.WriteLine($"Source table : {options.Table}");
Console.WriteLine($"Target folder: {Path.GetFullPath(options.DataDirectory)}");
Console.WriteLine($"Mode         : {(options.DryRun ? "dry run" : options.Overwrite ? "write, overwriting existing" : "write, keeping existing")}");
Console.WriteLine();

try
{
    var report = await new Migration(options).RunAsync(cancellation.Token);

    Console.WriteLine();
    Console.WriteLine("Done.");
    Console.WriteLine($"  rows read     : {report.RowsRead:N0}");
    Console.WriteLine($"  app keys      : {report.AppKeys:N0}");
    Console.WriteLine($"  keys written  : {report.KeysWritten:N0}");
    Console.WriteLine($"  keys skipped  : {report.KeysSkipped:N0}  (already present; use --overwrite to replace)");

    if (report.Normalized.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"Lowercased {report.Normalized.Count:N0} mixed-case client key(s):");
        foreach (var key in report.Normalized.Take(20)) Console.WriteLine($"  {key}");
        if (report.Normalized.Count > 20) Console.WriteLine($"  ... and {report.Normalized.Count - 20:N0} more");
    }

    if (report.Unmigratable.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine($"WARNING: {report.Unmigratable.Count:N0} client key(s) are not valid v2 app keys and were left behind.");
        Console.WriteLine("These need 8 characters of a-z/0-9. Their rows are untouched in SQL Server:");
        foreach (var key in report.Unmigratable.Take(20)) Console.WriteLine($"  '{key}'");
        if (report.Unmigratable.Count > 20) Console.WriteLine($"  ... and {report.Unmigratable.Count - 20:N0} more");
        return 1;
    }

    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled. Re-run to continue - app keys already copied will be skipped.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

// Echo enough of the connection string to confirm the right server, without the credential.
static string Describe(string connectionString)
{
    var server = connectionString
        .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(part =>
            part.StartsWith("Server=", StringComparison.OrdinalIgnoreCase) ||
            part.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase));

    return server ?? "(set)";
}
