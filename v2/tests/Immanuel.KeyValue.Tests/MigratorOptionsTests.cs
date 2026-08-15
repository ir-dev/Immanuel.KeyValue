using Immanuel.KeyValue.Migrator;
using Microsoft.Extensions.Configuration;

namespace Immanuel.KeyValue.Tests;

/// <summary>
/// Argument handling for the migration tool. The SQL Server read path needs a live v1 database
/// and is not covered here - run the tool with --dry-run against the real database first.
/// </summary>
public class MigratorOptionsTests
{
    [Fact]
    public void Parses_a_full_command_line()
    {
        var options = MigratorOptions.Parse([
            "--source", "Server=.;Database=immanuel_kv",
            "--data-dir", "/srv/keyvalue/App_Data",
            "--table", "[dbo].[KeyVal]",
            "--dry-run",
            "--overwrite",
        ]);

        Assert.Equal("Server=.;Database=immanuel_kv", options.Source);
        Assert.Equal("/srv/keyvalue/App_Data", options.DataDirectory);
        Assert.Equal("[dbo].[KeyVal]", options.Table);
        Assert.True(options.DryRun);
        Assert.True(options.Overwrite);
    }

    [Fact]
    public void Accepts_short_flags()
    {
        var options = MigratorOptions.Parse(["-s", "conn", "-d", "data", "-t", "tbl"]);

        Assert.Equal("conn", options.Source);
        Assert.Equal("data", options.DataDirectory);
        Assert.Equal("tbl", options.Table);
    }

    [Fact]
    public void Defaults_match_the_v1_deployment()
    {
        var options = MigratorOptions.Parse(["--source", "conn"]);

        Assert.Equal("[immanuel_sa].[KeyVal]", options.Table);
        Assert.Equal("App_Data", options.DataDirectory);
        Assert.False(options.DryRun);
        Assert.False(options.Overwrite);
        Assert.True(options.NormalizeCase);
    }

    [Fact]
    public void Requires_a_source()
    {
        var error = Assert.Throws<ArgumentException>(() => MigratorOptions.Parse([]));
        Assert.Contains("KEYVALUE_SOURCE", error.Message);
    }

    [Fact]
    public void Rejects_a_flag_with_no_value()
    {
        Assert.Throws<ArgumentException>(() => MigratorOptions.Parse(["--source"]));
    }

    [Fact]
    public void Rejects_unknown_flags()
    {
        var error = Assert.Throws<ArgumentException>(() => MigratorOptions.Parse(["--source", "c", "--wat"]));
        Assert.Contains("--wat", error.Message);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_is_signalled_separately_from_an_error(string flag)
    {
        Assert.Throws<MigratorOptions.HelpRequested>(() => MigratorOptions.Parse([flag]));
    }

    // ---------- configuration ----------

    private static IConfiguration Config(params (string Key, string Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => (string?)e.Value))
            .Build();

    [Fact]
    public void Reads_everything_from_configuration()
    {
        var options = MigratorOptions.Parse([], Config(
            ("ConnectionStrings:KeyValueSource", "Server=from-config,1433;Database=immanuel_kv"),
            ("KeyValue:DataDirectory", "/srv/keyvalue/App_Data"),
            ("KeyValue:Table", "[dbo].[KeyVal]")));

        Assert.Equal("Server=from-config,1433;Database=immanuel_kv", options.Source);
        Assert.Equal("/srv/keyvalue/App_Data", options.DataDirectory);
        Assert.Equal("[dbo].[KeyVal]", options.Table);
        Assert.Equal("configuration", options.SourceOrigin);
    }

    [Fact]
    public void Command_line_beats_configuration()
    {
        var options = MigratorOptions.Parse(
            ["--source", "Server=from-cli", "--data-dir", "/from/cli", "--table", "[cli].[Table]"],
            Config(
                ("ConnectionStrings:KeyValueSource", "Server=from-config"),
                ("KeyValue:DataDirectory", "/from/config"),
                ("KeyValue:Table", "[config].[Table]")));

        Assert.Equal("Server=from-cli", options.Source);
        Assert.Equal("/from/cli", options.DataDirectory);
        Assert.Equal("[cli].[Table]", options.Table);
        Assert.Equal("--source", options.SourceOrigin);
    }

    [Fact]
    public void Blank_configuration_values_do_not_override_the_defaults()
    {
        // appsettings.json ships with an empty KeyValueSource; it must not look like a real value.
        var options = MigratorOptions.Parse(["--source", "conn"], Config(
            ("ConnectionStrings:KeyValueSource", ""),
            ("KeyValue:Table", "")));

        Assert.Equal("conn", options.Source);
        Assert.Equal("[immanuel_sa].[KeyVal]", options.Table);
    }

    [Fact]
    public void An_empty_configured_source_still_counts_as_missing()
    {
        var error = Assert.Throws<ArgumentException>(
            () => MigratorOptions.Parse([], Config(("ConnectionStrings:KeyValueSource", "   "))));

        Assert.Contains("appsettings.Development.json", error.Message);
    }
}
