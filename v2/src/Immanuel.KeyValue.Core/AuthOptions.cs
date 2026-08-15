namespace Immanuel.KeyValue.Core;

/// <summary>
/// Accounts and sign-in, under the "Auth" section of appsettings.json.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    /// <summary>
    /// Turns account sign-up off entirely. The anonymous v1/v2 endpoints keep working - this only
    /// closes the /api/v2/auth and /api/v2/me routes.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The code accepted for every account when <see cref="Smtp"/> is not configured, because
    /// there is then no way to deliver a real one. This is a development and self-hosting
    /// convenience: anyone who knows it can sign in as any address, so configure SMTP before
    /// putting the service anywhere public.
    /// </summary>
    public string MasterOtp { get; set; } = "000000";

    /// <summary>
    /// Return the master code in the sign-in response so the console can fill it in. Handy while
    /// developing and dangerous in production, which is why it defaults to off and is ignored the
    /// moment SMTP is configured.
    /// </summary>
    public bool RevealMasterOtp { get; set; }

    /// <summary>How long a code stays valid.</summary>
    public int OtpLifetimeMinutes { get; set; } = 10;

    /// <summary>Wrong guesses allowed before the code is thrown away. Six digits is only 10^6
    /// combinations, so this is what makes them worth using.</summary>
    public int OtpMaxAttempts { get; set; } = 5;

    /// <summary>How long a console session lasts before the user signs in again.</summary>
    public int SessionLifetimeHours { get; set; } = 336;

    /// <summary>Soft quota on app keys per account, so one sign-up cannot fill the disk with
    /// empty databases.</summary>
    public int MaxAppKeysPerUser { get; set; } = 10;

    /// <summary>
    /// Custom header names an account may not claim, because the server or a proxy in front of
    /// it gives them their own meaning. Matched case-insensitively.
    /// </summary>
    public string[] ReservedHeaderNames { get; set; } =
    [
        "authorization", "cookie", "host", "content-type", "content-length", "accept",
        "origin", "referer", "user-agent", "x-forwarded-for", "x-forwarded-proto",
    ];

    public SmtpOptions Smtp { get; set; } = new();

    public TimeSpan OtpLifetime => TimeSpan.FromMinutes(Math.Max(1, OtpLifetimeMinutes));

    public TimeSpan SessionLifetime => TimeSpan.FromHours(Math.Max(1, SessionLifetimeHours));
}

/// <summary>
/// Where one-time passwords are sent from. Leave <see cref="Host"/> empty and the service falls
/// back to <see cref="AuthOptions.MasterOtp"/>.
/// </summary>
public sealed class SmtpOptions
{
    public string? Host { get; set; }

    public int Port { get; set; } = 587;

    /// <summary>STARTTLS. Off only makes sense for a relay on localhost.</summary>
    public bool UseSsl { get; set; } = true;

    public string? UserName { get; set; }

    public string? Password { get; set; }

    /// <summary>The From address. Most relays reject mail that does not match the account.</summary>
    public string? FromAddress { get; set; }

    public string FromName { get; set; } = "Immanuel KeyValue";

    /// <summary>Whether there is enough here to attempt a delivery at all.</summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(FromAddress);
}
