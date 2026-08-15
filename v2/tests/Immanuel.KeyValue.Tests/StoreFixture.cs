using Immanuel.KeyValue.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Immanuel.KeyValue.Tests;

/// <summary>
/// A real store over real SQLite files in a throwaway folder. These are not mocks on purpose:
/// most of the behaviour worth testing here lives in the SQL.
/// </summary>
public sealed class StoreFixture : IDisposable
{
    public string DataDirectory { get; }
    public KeyValueStore Store { get; }
    public AppKeyCatalog Catalog { get; }
    public SqliteStoreFactory Factory { get; }

    public StoreFixture(Action<KeyValueOptions>? configure = null)
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "kv-tests", Guid.NewGuid().ToString("n"));

        var options = new KeyValueOptions { DataDirectory = DataDirectory };
        configure?.Invoke(options);

        Factory = new SqliteStoreFactory(
            Options.Create(options), DataDirectory, NullLogger<SqliteStoreFactory>.Instance);

        Catalog = new AppKeyCatalog(Factory);
        Store = new KeyValueStore(Factory, Catalog, Options.Create(options), NullLogger<KeyValueStore>.Instance);
    }

    public void Dispose()
    {
        // SQLite connection pooling can still hold the files briefly; a failed cleanup of a
        // temp folder must not fail the test run.
        try
        {
            if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
