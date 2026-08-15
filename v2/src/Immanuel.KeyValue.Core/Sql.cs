namespace Immanuel.KeyValue.Core;

/// <summary>Small helpers for talking to Ark.Rapid.Database.</summary>
public static class Sql
{
    /// <summary>
    /// Ark's column dictionaries are typed <c>Dictionary&lt;string, object&gt;</c>, and its value
    /// formatter treats <see cref="DBNull"/> exactly like a C# null. Routing optional values
    /// through here keeps "no value" meaning SQL NULL without fighting nullable reference types.
    /// </summary>
    public static object OrNull(object? value) => value ?? DBNull.Value;

    /// <summary>
    /// True when the SQL expression <paramref name="valueExpression"/> holds a whole number,
    /// optionally signed. This is the SQLite stand-in for v1's ISNUMERIC() guard on increment.
    ///
    /// Strip a leading '-', then require the remainder to start with a digit (which also rules
    /// out the empty string) and to contain nothing but digits.
    /// </summary>
    public static string IsWholeNumber(string valueExpression)
    {
        var digits = $"CASE WHEN {valueExpression} GLOB '-*' THEN substr({valueExpression}, 2) ELSE {valueExpression} END";
        return $"({digits} GLOB '[0-9]*' AND {digits} NOT GLOB '*[^0-9]*')";
    }
}
