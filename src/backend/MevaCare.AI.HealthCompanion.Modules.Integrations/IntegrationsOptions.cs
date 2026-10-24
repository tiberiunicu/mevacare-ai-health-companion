namespace MevaCare.AI.HealthCompanion.Modules.Integrations;

public sealed class IntegrationsOptions
{
    public const string Section = "Integrations";

    public WhoopOptions Whoop { get; set; } = new();

    public SyncOptions Sync { get; set; } = new();

    /// <summary>Where the browser is sent after the OAuth callback (the SPA). Never taken from a request.</summary>
    public string FrontendBaseUrl { get; set; } = "http://localhost:5173";

    /// <summary>
    /// Folder for the Data Protection key ring that encrypts stored provider tokens. When empty the framework default is used
    /// (per-user profile, DPAPI-protected on Windows). In production set it to a protected, persistent, backed-up location:
    /// losing the keys makes every stored token unreadable and every account must be reconnected.
    /// </summary>
    public string? DataProtectionKeysPath { get; set; }
}

public enum ClientAuthentication
{
    /// <summary>client_id and client_secret in the form body (the only form shown in WHOOP's own refresh example).</summary>
    Post = 1,

    /// <summary>HTTP Basic authentication of the client.</summary>
    Basic = 2,
}

public sealed class WhoopOptions
{
    /// <summary>From the WHOOP developer dashboard. The secret belongs in user-secrets / environment variables only.</summary>
    public string ClientId { get; set; } = "";

    public string ClientSecret { get; set; } = "";

    /// <summary>Must match a redirect URL registered in the WHOOP dashboard exactly. It points to this API's callback endpoint.</summary>
    public string RedirectUri { get; set; } = "";

    public string AuthorizationEndpoint { get; set; } = "https://api.prod.whoop.com/oauth/oauth2/auth";
    public string TokenEndpoint { get; set; } = "https://api.prod.whoop.com/oauth/oauth2/token";

    /// <summary>Base of the data API, ending in a slash; paths such as "v2/activity/sleep" are appended.</summary>
    public string ApiBaseUrl { get; set; } = "https://api.prod.whoop.com/developer/";

    /// <summary>"offline" is required to receive a refresh token. Only read scopes for the data the application uses.</summary>
    public string Scopes { get; set; } = "offline read:profile read:recovery read:cycles read:sleep read:workout";

    public ClientAuthentication ClientAuthentication { get; set; } = ClientAuthentication.Post;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret)
        && Uri.TryCreate(RedirectUri, UriKind.Absolute, out var redirect) && redirect.Scheme is "http" or "https";
}

public sealed class SyncOptions
{
    /// <summary>Periodic synchronization of connected accounts. Turned off in tests.</summary>
    public bool BackgroundEnabled { get; set; } = true;

    /// <summary>How often the background worker looks for accounts that are due.</summary>
    public int PollMinutes { get; set; } = 15;

    /// <summary>An account is synchronized again when its last successful sync is older than this.</summary>
    public int IntervalMinutes { get; set; } = 60;

    /// <summary>History fetched on the first synchronization.</summary>
    public int InitialBackfillDays { get; set; } = 30;

    /// <summary>Later synchronizations re-read this many days before the last success, so records scored late are picked up.</summary>
    public int OverlapDays { get; set; } = 7;

    /// <summary>Safety cap on pages fetched per collection and run.</summary>
    public int MaxPagesPerCollection { get; set; } = 100;

    /// <summary>Minimum time between two user-triggered synchronizations of the same account.</summary>
    public int MinManualIntervalSeconds { get; set; } = 60;
}
