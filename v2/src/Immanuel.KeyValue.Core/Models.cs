namespace Immanuel.KeyValue.Core;

/// <summary>How an operation ended. Expected outcomes are values, not exceptions, so callers
/// can map them straight onto HTTP status codes.</summary>
public enum StoreStatus
{
    Ok,
    /// <summary>The app key was not 8 characters of [a-z0-9], or the key/value broke a length limit.</summary>
    Invalid,
    /// <summary>No database exists for that app key - it was never issued, or it was deleted.</summary>
    AppKeyNotFound,
    KeyNotFound,
    /// <summary>Increment/decrement was asked for on a value that is not a whole number.</summary>
    NotNumeric,
    /// <summary>Adding another key would exceed <see cref="KeyValueOptions.MaxKeysPerAppKey"/>.</summary>
    KeyLimitReached,
}

/// <summary>One stored key, as it comes back from the database.</summary>
public sealed class KeyValueEntry
{
    public string KeyName { get; set; } = "";
    public string? KeyVal { get; set; }
    public string CreatedAt { get; set; } = "";
    public string UpdatedAt { get; set; } = "";
}

/// <summary>A registered app key, as it comes back from the catalog.</summary>
public sealed class AppKeyInfo
{
    public string ClientKey { get; set; } = "";
    public long KeyCount { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? LastAccessAt { get; set; }
}

/// <summary>Service-wide totals.</summary>
public sealed class StoreStats
{
    public long AppKeys { get; set; }
    public long Keys { get; set; }
}

/// <summary>Outcome of a set/write. <paramref name="Created"/> distinguishes a new key from an overwrite.</summary>
public readonly record struct SetResult(StoreStatus Status, bool Created)
{
    public bool IsOk => Status == StoreStatus.Ok;
}

/// <summary>Outcome of an increment/decrement, carrying the value the key ended up at.</summary>
public readonly record struct AdjustResult(StoreStatus Status, string? Value)
{
    public bool IsOk => Status == StoreStatus.Ok;
}
