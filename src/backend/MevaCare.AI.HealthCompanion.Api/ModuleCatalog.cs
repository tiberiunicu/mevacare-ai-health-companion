using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Modules.Assistant;
using MevaCare.AI.HealthCompanion.Modules.HealthProfile;
using MevaCare.AI.HealthCompanion.Modules.Identity;
using MevaCare.AI.HealthCompanion.Modules.Insights;
using MevaCare.AI.HealthCompanion.Modules.Integrations;
using MevaCare.AI.HealthCompanion.Modules.Knowledge;
using MevaCare.AI.HealthCompanion.Modules.Measurements;
using MevaCare.AI.HealthCompanion.Modules.Nutrition;

namespace MevaCare.AI.HealthCompanion.Api;

/// <summary>The explicit list of modules composing the monolith. Adding a module = adding one line here.</summary>
internal static class ModuleCatalog
{
    public static IReadOnlyList<IModule> CreateAll() =>
    [
        new IdentityModule(),
        new HealthProfileModule(),
        new NutritionModule(),
        new MeasurementsModule(),
        new IntegrationsModule(),
        new InsightsModule(),
        new KnowledgeModule(),
        new AssistantModule(),
    ];
}
