namespace MevaCare.AI.HealthCompanion.Contracts.Abstractions;

/// <summary>Names of the rate-limiting policies registered by the host (see Api/Program.cs).</summary>
public static class RateLimitPolicies
{
    /// <summary>Per-IP limit for anonymous credential endpoints (register, login, refresh).</summary>
    public const string Auth = "auth";

    /// <summary>Per-user limit for endpoints that call a paid LLM.</summary>
    public const string Chat = "chat";
}
