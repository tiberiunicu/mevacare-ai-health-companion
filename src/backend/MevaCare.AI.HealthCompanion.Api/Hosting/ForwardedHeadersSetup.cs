using Microsoft.AspNetCore.HttpOverrides;

namespace MevaCare.AI.HealthCompanion.Api.Hosting;

/// <summary>
/// Behind a reverse proxy every request reaches the API from the proxy's address. Without this the per-IP rate limits would treat all
/// visitors as one person, and HSTS and the secure-cookie checks would see plain http. The proxy's <c>X-Forwarded-For</c> /
/// <c>X-Forwarded-Proto</c> headers are honoured only when the connection comes from a trusted network (by default the private ranges
/// used between containers), so a visitor who connects from the internet cannot forge them. Several proxies in a chain are supported
/// (each appends its client); processing stops at the first address that is not trusted, which is the real visitor.
/// </summary>
public static class ForwardedHeadersSetup
{
    public const string SectionName = "ForwardedHeaders";

    /// <summary>Loopback and the private ranges Docker uses.</summary>
    public static readonly string[] DefaultTrustedNetworks = ["127.0.0.0/8", "::1/128", "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"];

    public static void Configure(IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        if (!section.GetValue("Enabled", true))
        {
            return;
        }

        var networks = section.GetSection("TrustedNetworks").Get<string[]>() is { Length: > 0 } configured ? configured : DefaultTrustedNetworks;

        services.Configure<ForwardedHeadersOptions>(options => Apply(options, networks));
    }

    public static void Apply(ForwardedHeadersOptions options, IEnumerable<string> trustedNetworks)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = null; // walk the whole chain; the trusted-network list decides where it stops
        options.RequireHeaderSymmetry = false;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var network in trustedNetworks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
    }

    public static bool IsEnabled(IConfiguration configuration) => configuration.GetSection(SectionName).GetValue("Enabled", true);
}
