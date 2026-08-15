namespace Immanuel.KeyValue.Core;

/// <summary>
/// Everything you can tune from appsettings.json, under the "KeyValue" section.
/// </summary>
public sealed class KeyValueOptions
{
    public const string SectionName = "KeyValue";

    /// <summary>
    /// Folder holding one SQLite file per app key (plus the catalog). Relative paths are
    /// resolved against the application's content root.
    /// </summary>
    public string DataDirectory { get; set; } = "App_Data";

    /// <summary>Matches v1's varchar(64) KeyName column.</summary>
    public int MaxKeyLength { get; set; } = 64;

    /// <summary>Matches v1's varchar(1024) KeyVal column.</summary>
    public int MaxValueLength { get; set; } = 1024;

    /// <summary>
    /// Soft quota so one app key cannot fill the disk. v1 had no limit; raise it if you
    /// have users who legitimately store more than this.
    /// </summary>
    public int MaxKeysPerAppKey { get; set; } = 1000;

    /// <summary>
    /// v1 let any 8-character string act as an app key, because the row was simply created
    /// on first write. v2 issues app keys explicitly, so writing to a key that was never
    /// issued is rejected. Set this to true to get the old permissive behaviour back.
    /// </summary>
    public bool AutoCreateUnknownAppKeys { get; set; }
}
