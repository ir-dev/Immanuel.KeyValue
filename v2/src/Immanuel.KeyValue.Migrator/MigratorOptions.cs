using Microsoft.Extensions.Configuration;

namespace Immanuel.KeyValue.Migrator;

/// <summary>Everything the migration run needs.</summary>
public sealed class MigratorOptions
{
    /// <summary>SQL Server connection string for the v1 database.</summary>
    public string Source { get; set; } = "";

    /// <summary>Folder that will receive one SQLite file per app key.</summary>
    public string DataDirectory { get; set; } = "App_Data";

    /// <summary>The v1 table. Only change this if you renamed it.</summary>
    public string Table { get; set; } = "[immanuel_sa].[KeyVal]";

    /// <summary>Read and report, write nothing.</summary>
    public bool DryRun { get; set; }

    /// <summary>Replace values that already exist in SQLite. Off by default, so re-running
    /// after a partial migration only fills in what is missing.</summary>
    public bool Overwrite { get; set; }

    /// <summary>
    /// v1's ClientKey was a plain varchar(8), so a few rows may hold keys that are not valid
    /// v2 app keys. When set, keys that only differ by case are lowercased instead of skipped.
    /// </summary>
    public bool NormalizeCase { get; set; } = true;

    /// <summary>Where <see cref="Source"/> came from, so the run can say so without printing it.</summary>
    public string SourceOrigin { get; private set; } = "nowhere";

    /// <summary>
    /// Settings come from appsettings.json, then appsettings.{Environment}.json, then
    /// environment variables, and finally the command line - each layer overriding the last.
    /// Put the connection string in appsettings.Development.json, which is git-ignored.
    /// </summary>
    public static MigratorOptions Parse(string[] args, IConfiguration? configuration = null)
    {
        var options = new MigratorOptions();

        if (configuration is not null)
        {
            var fromConfig = configuration.GetConnectionString("KeyValueSource");
            if (!string.IsNullOrWhiteSpace(fromConfig))
            {
                options.Source = fromConfig;
                options.SourceOrigin = "configuration";
            }

            var dataDirectory = configuration["KeyValue:DataDirectory"];
            if (!string.IsNullOrWhiteSpace(dataDirectory)) options.DataDirectory = dataDirectory;

            var table = configuration["KeyValue:Table"];
            if (!string.IsNullOrWhiteSpace(table)) options.Table = table;
        }

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source" or "-s":
                    options.Source = Next(args, ref i);
                    options.SourceOrigin = "--source";
                    break;
                case "--data-dir" or "-d": options.DataDirectory = Next(args, ref i); break;
                case "--table" or "-t": options.Table = Next(args, ref i); break;
                case "--dry-run": options.DryRun = true; break;
                case "--overwrite": options.Overwrite = true; break;
                case "--no-normalize-case": options.NormalizeCase = false; break;
                case "--help" or "-h": throw new HelpRequested();
                default: throw new ArgumentException($"Unrecognised argument '{args[i]}'. Try --help.");
            }
        }

        if (string.IsNullOrWhiteSpace(options.Source))
        {
            throw new ArgumentException(
                "No source database. Set ConnectionStrings:KeyValueSource in appsettings.Development.json, "
                + "or set the KEYVALUE_SOURCE environment variable, or pass --source \"<connection string>\".");
        }

        return options;
    }

    private static string Next(string[] args, ref int i)
    {
        if (i + 1 >= args.Length) throw new ArgumentException($"'{args[i]}' needs a value.");
        return args[++i];
    }

    public sealed class HelpRequested : Exception;

    public const string HelpText = """
        Immanuel.KeyValue migration - copies the v1 SQL Server table into v2's per-app-key
        SQLite databases. Safe to re-run: existing values are left alone unless --overwrite.

        The connection string normally lives in appsettings.Development.json, which is
        git-ignored. Copy appsettings.Development.json.example to get started. Anything below
        overrides what is in the file.

          --source, -s <connection string>   v1 SQL Server database. Overrides configuration.
          --data-dir, -d <path>              Where the .db files go.
                                             Point this at the web app's DataDirectory.
          --table, -t <name>                 Default: [immanuel_sa].[KeyVal]
          --dry-run                          Report what would happen, write nothing.
          --overwrite                        Replace values that already exist in SQLite.
          --no-normalize-case                Skip mixed-case client keys instead of lowercasing.
          --help, -h                         This text.

        Configuration keys (appsettings.json / appsettings.Development.json):
          ConnectionStrings:KeyValueSource   The v1 SQL Server connection string.
          KeyValue:DataDirectory             Where the .db files go.
          KeyValue:Table                     The v1 table.

        Environment variables:
          KEYVALUE_SOURCE                    Same as ConnectionStrings:KeyValueSource.

        Note: use "Server=host,1433", not "Server=host\\INSTANCE". Named-instance lookup
        needs UDP 1434, which is usually blocked over the internet (SQL error 26).

        Example:
          dotnet run -- --dry-run
          dotnet run -- --data-dir ../Immanuel.KeyValue.Web/App_Data
        """;
}
