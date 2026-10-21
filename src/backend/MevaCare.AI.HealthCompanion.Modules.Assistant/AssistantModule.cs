using System.Net.ServerSentEvents;
using System.Text.Json;
using FluentValidation;
using MevaCare.AI.HealthCompanion.Contracts.Abstractions;
using MevaCare.AI.HealthCompanion.Contracts.Validation;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Features;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Options;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Providers;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Tools;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant;

public sealed class AssistantModule : IModule
{
    private static readonly JsonSerializerOptions EventJson = new(JsonSerializerDefaults.Web);

    public string Name => "Assistant";

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.SectionName))
            .ValidateDataAnnotations().ValidateOnStart();

        // Created lazily on first use: a missing provider configuration yields HTTP 503 "ai_not_configured"
        // for chat only and never prevents the application from starting.
        services.AddSingleton<IChatClient>(sp => ChatClientFactory.Create(sp.GetRequiredService<IOptions<AiOptions>>().Value));

        services.AddScoped<AiChatService>();
        services.AddScoped<ConversationService>();
        services.AddScoped<MealParser>();
        services.AddScoped<IMealProposalGate, ProposalGate>();
        services.AddScoped<ProposalService>();
        services.AddScoped<IUserDataExporter, AssistantExporter>();
        services.AddValidatorsFromAssemblyContaining<AssistantModule>();
    }

    public void MapEndpoints(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/chat/stream", async (SendMessageRequest request, ICurrentUser user, AiChatService chat, HttpContext http, CancellationToken ct) =>
            {
                // All pre-flight failures (consent, validation, foreign conversation, duplicate, provider not configured)
                // are raised here, before the streaming response starts, and become normal problem documents.
                var turn = await chat.PrepareTurnAsync(user.UserId, request, ct);

                http.Response.Headers["X-Accel-Buffering"] = "no"; // do not let reverse proxies buffer the stream
                return TypedResults.ServerSentEvents(ToSse(chat.StreamAsync(turn, ct), ct));
            })
            .WithTags("Chat")
            .RequireRateLimiting(RateLimitPolicies.Chat)
            .WithValidation<SendMessageRequest>();

        MapProposals(app);

        var conversations = app.MapGroup("/api/conversations").WithTags("Chat");
        conversations.MapGet("/", async (ICurrentUser user, ConversationService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(user.UserId, ct)));
        conversations.MapGet("/{id:guid}/messages", async (Guid id, ICurrentUser user, ConversationService service, CancellationToken ct) =>
            TypedResults.Ok(await service.GetMessagesAsync(user.UserId, id, ct)));
        conversations.MapGet("/{id:guid}/proposals", async (Guid id, ICurrentUser user, ProposalService service, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(user.UserId, id, ct)));
        conversations.MapDelete("/{id:guid}", async (Guid id, ICurrentUser user, ConversationService service, CancellationToken ct) =>
        {
            await service.DeleteAsync(user.UserId, id, ct);
            return TypedResults.NoContent();
        });
    }

    private static void MapProposals(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/assistant/proposals/{id:guid}/dismiss", async (Guid id, ICurrentUser user, ProposalService service, CancellationToken ct) =>
            {
                await service.DismissAsync(user.UserId, id, ct);
                return TypedResults.NoContent();
            })
            .WithTags("Chat");
    }

    private static async IAsyncEnumerable<SseItem<string>> ToSse(
        IAsyncEnumerable<ChatStreamEvent> events,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in events.WithCancellation(ct))
        {
            yield return new SseItem<string>(JsonSerializer.Serialize(evt, evt.GetType(), EventJson), evt.EventName);
        }
    }
}
