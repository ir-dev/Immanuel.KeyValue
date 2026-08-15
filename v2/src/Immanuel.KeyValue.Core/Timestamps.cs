using System.Globalization;

namespace Immanuel.KeyValue.Core;

/// <summary>
/// The C# side of <see cref="Schema.UtcNow"/>. Everything stored as a timestamp is ISO-8601 UTC
/// text in exactly this shape, so string comparison in SQL orders the same way the instants do -
/// which is what lets "ExpiresAt &gt; now" be an ordinary WHERE clause.
/// </summary>
public static class Timestamps
{
    public const string Format8601 = "yyyy-MM-ddTHH:mm:ssZ";

    public static string Format(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString(Format8601, CultureInfo.InvariantCulture);

    public static string UtcNow() => Format(DateTimeOffset.UtcNow);

    /// <summary>Reads a stored timestamp back. Returns null rather than throwing on anything
    /// unparseable, so one bad row cannot take a request down.</summary>
    public static DateTimeOffset? Parse(string? stored) =>
        DateTimeOffset.TryParse(
            stored, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
}
