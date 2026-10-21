using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Modules.Assistant;
using MevaCare.AI.HealthCompanion.Modules.HealthProfile;
using MevaCare.AI.HealthCompanion.Modules.Identity;

namespace MevaCare.AI.HealthCompanion.Api;

/// <summary>The explicit list of modules composing the monolith. Adding a module = adding one line here.</summary>
internal static class ModuleCatalog
{
    public static IReadOnlyList<IModule> CreateAll() =>
    [
        new IdentityModule(),
        new HealthProfileModule(),
        new AssistantModule(),
    ];
}
