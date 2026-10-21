using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MevaCare.AI.HealthCompanion.Contracts.Abstractions;

/// <summary>
/// Entry point of a module. The host discovers modules explicitly (no assembly scanning magic),
/// registers their services, applies their EF Core configurations and maps their endpoints.
/// </summary>
public interface IModule
{
    string Name { get; }

    /// <summary>Assembly containing the module's IEntityTypeConfiguration classes.</summary>
    Assembly ModelAssembly => GetType().Assembly;

    void ConfigureServices(IServiceCollection services, IConfiguration configuration);

    void MapEndpoints(IEndpointRouteBuilder app);
}
