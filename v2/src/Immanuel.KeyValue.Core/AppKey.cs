using System.Security.Cryptography;

namespace Immanuel.KeyValue.Core;

/// <summary>
/// An app key doubles as the file name of that user's SQLite database, so validating it is a
/// security boundary rather than a nicety: anything outside [a-z0-9]{8} is rejected before it
/// can ever reach the file system. That rules out "../", absolute paths and reserved names.
/// </summary>
public static class AppKey
{
    public const int Length = 8;

    // The same alphabet v1 used, so keys issued by the old service still validate here.
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>
    /// v1 used a shared System.Random, which made issued keys predictable. This uses a
    /// cryptographic RNG instead - same shape, same length, no longer guessable.
    /// </summary>
    public static string Generate() => RandomNumberGenerator.GetString(Alphabet, Length);

    public static bool IsValid(string? appKey)
    {
        if (appKey is null || appKey.Length != Length) return false;

        foreach (var c in appKey)
        {
            var allowed = c is >= 'a' and <= 'z' || c is >= '0' and <= '9';
            if (!allowed) return false;
        }

        return true;
    }
}
