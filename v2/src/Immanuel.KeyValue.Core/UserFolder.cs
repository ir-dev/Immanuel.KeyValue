namespace Immanuel.KeyValue.Core;

/// <summary>
/// Maps a signed-up email address onto the folder that holds that user's app-key databases:
/// <c>raj@immanuel.co</c> becomes <c>raj_at_immanuel.co</c>.
///
/// Like <see cref="AppKey"/>, the result is a path segment, so validation here is a security
/// boundary rather than a nicety. Everything is checked against a deliberately narrow character
/// set before it can reach the file system, which rules out "..", separators, absolute paths and
/// the reserved leading underscore the catalog files use.
/// </summary>
public static class UserFolder
{
    /// <summary>What "@" becomes. Chosen because "_" cannot appear in a domain, so the last
    /// occurrence is always the separator and the mapping reverses unambiguously.</summary>
    public const string AtToken = "_at_";

    public const int MaxEmailLength = 254;

    /// <summary>Trims and lowercases, then returns null if the result is not an email we accept.</summary>
    public static string? NormalizeEmail(string? email)
    {
        if (email is null) return null;

        var trimmed = email.Trim().ToLowerInvariant();
        return IsValidEmail(trimmed) ? trimmed : null;
    }

    /// <summary>
    /// A deliberately conservative subset of RFC 5321: lowercase letters, digits and
    /// <c>. _ % + -</c> before the "@", and a dotted domain of letters, digits and hyphens after
    /// it. Quoted local parts and internationalised domains are refused rather than mangled into
    /// a folder name.
    /// </summary>
    public static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrEmpty(email) || email.Length > MaxEmailLength) return false;

        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@') || at == email.Length - 1) return false;

        var local = email.AsSpan(0, at);
        var domain = email.AsSpan(at + 1);

        if (local.Length > 64 || !IsCleanPart(local, "._%+-")) return false;
        if (!IsCleanPart(domain, ".-")) return false;

        // A dotted domain with an alphabetic last label - "user@localhost" would otherwise
        // produce a folder that reads like a bare word.
        var lastDot = domain.LastIndexOf('.');
        if (lastDot <= 0 || domain.Length - lastDot - 1 < 2) return false;

        foreach (var c in domain[(lastDot + 1)..])
        {
            if (c is < 'a' or > 'z') return false;
        }

        return true;
    }

    /// <summary>The folder name for an email, or null when the email is not one we accept.</summary>
    public static string? ToFolderName(string? email)
    {
        var normalized = NormalizeEmail(email);
        if (normalized is null) return null;

        var folder = normalized.Replace("@", AtToken, StringComparison.Ordinal);
        return IsValidFolderName(folder) ? folder : null;
    }

    /// <summary>
    /// Whether a directory name is one of ours. Used both before touching the file system and
    /// when scanning the data directory, so a folder dropped in by hand cannot be mistaken for
    /// a user's store.
    /// </summary>
    public static bool IsValidFolderName(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return false;

        // The reverse mapping has to succeed, which is what rules out separators, "..",
        // leading underscores and anything else outside the accepted character set.
        return ToEmail(folder) is not null;
    }

    /// <summary>
    /// The email a folder name came from, or null when the folder is not one of ours. Splits on
    /// the <em>last</em> "_at_" because a local part may legitimately contain one
    /// (<c>foo_at_bar@example.com</c>) while a domain never can.
    /// </summary>
    public static string? ToEmail(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return null;

        var split = folder.LastIndexOf(AtToken, StringComparison.Ordinal);
        if (split <= 0) return null;

        var email = string.Concat(folder.AsSpan(0, split), "@", folder.AsSpan(split + AtToken.Length));
        return IsValidEmail(email) ? email : null;
    }

    private static bool IsCleanPart(ReadOnlySpan<char> part, ReadOnlySpan<char> punctuation)
    {
        if (part.Length == 0) return false;

        // A leading "_" or "." would produce a hidden folder, or one that looks like the
        // underscore-prefixed catalog files. Trailing punctuation is refused for the same reason
        // Windows refuses trailing dots in a path segment.
        if (part[0] is '.' or '-' or '_' || part[^1] is '.' or '-') return false;

        if (part.Contains("..", StringComparison.Ordinal)) return false;

        foreach (var c in part)
        {
            var allowed = c is >= 'a' and <= 'z' || c is >= '0' and <= '9' || punctuation.Contains(c);
            if (!allowed) return false;
        }

        return true;
    }
}
