using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using MevaCare.AI.HealthCompanion.Contracts.Nutrition;
using MevaCare.AI.HealthCompanion.Infrastructure.Persistence;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Domain;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Features;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Tools;

/// <summary>
/// The tools the model may call during ONE chat turn. The instance is created per turn and is bound to the
/// authenticated user's id: no tool has a user-id parameter, so the model cannot address anyone else's data, and each
/// underlying service re-checks consent and filters by that id. Every call is audited in ToolExecutions. Failures are
/// returned to the model as small error objects (it can explain them) and never as exceptions or provider/DB details.
/// </summary>
public sealed class TurnTools
{
    private readonly Guid _userId;
    private readonly Guid _conversationId;
    private readonly int _assistantSequence;
    private readonly INutritionAssistantApi _nutrition;
    private readonly IHealthProfileReader _profiles;
    private readonly MealParser _parser;
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    public TurnTools(
        Guid userId, Guid conversationId, int assistantSequence, INutritionAssistantApi nutrition, IHealthProfileReader profiles,
        MealParser parser, AppDbContext db, TimeProvider clock, ILogger logger)
    {
        _userId = userId;
        _conversationId = conversationId;
        _assistantSequence = assistantSequence;
        _nutrition = nutrition;
        _profiles = profiles;
        _parser = parser;
        _db = db;
        _clock = clock;
        _logger = logger;

        Functions =
        [
            AIFunctionFactory.Create(GetHealthProfileAsync, "GetHealthProfile",
                "Returns the user's saved health profile facts (age, sex, height, activity level, goal types). Read-only."),
            AIFunctionFactory.Create(GetDailyNutritionSummaryAsync, "GetDailyNutritionSummary",
                "Returns what the user logged on one day: totals of calories, protein, carbohydrates, fat and fibre, their targets and what remains. Read-only."),
            AIFunctionFactory.Create(GetNutritionSummaryAsync, "GetNutritionSummary",
                "Returns daily totals and the average per logged day between two dates (at most 366 days). Read-only."),
            AIFunctionFactory.Create(PrepareMealAsync, ToolExecution.PrepareMealTool,
                "Prepares a meal from the user's own words. It only shows the user a proposal card to review; NOTHING is saved until the user confirms in the app."),
        ];
    }

    public IReadOnlyList<AITool> Functions { get; }

    /// <summary>Events produced by tools (proposal cards) for the stream to forward to the browser.</summary>
    public Queue<ChatStreamEvent> PendingEvents { get; } = new();

    public int ProposalCount { get; private set; }

    // ---- tools ----

    private Task<object> GetHealthProfileAsync(CancellationToken ct) =>
        RunAsync("GetHealthProfile", ToolKind.Read, new { }, async () => Result(await _profiles.GetSummaryAsync(_userId, ct)));

    private Task<object> GetDailyNutritionSummaryAsync(
        [Description("The day as YYYY-MM-DD, in the user's local calendar.")] string date, CancellationToken ct) =>
        RunAsync("GetDailyNutritionSummary", ToolKind.Read, new { date }, async () =>
        {
            var summary = await _nutrition.GetDailySummaryAsync(_userId, ParseDate(date), ct);
            return Result(new
            {
                summary.Date,
                summary.Totals,
                summary.Targets,
                summary.Remaining,
                Meals = summary.Meals.Select(m => new
                {
                    m.MealType,
                    Items = m.Items.Select(i => new { i.Name, i.QuantityGrams, i.Nutrients.Calories, i.IsEstimated }),
                }),
            });
        });

    private Task<object> GetNutritionSummaryAsync(
        [Description("First day, YYYY-MM-DD.")] string from, [Description("Last day, YYYY-MM-DD.")] string to, CancellationToken ct) =>
        RunAsync("GetNutritionSummary", ToolKind.Read, new { from, to }, async () =>
            Result(await _nutrition.GetRangeSummaryAsync(_userId, ParseDate(from), ParseDate(to), ct)));

    private Task<object> PrepareMealAsync(
        [Description("What the user said they ate, in their own words, including amounts.")] string description, CancellationToken ct) =>
        RunAsync(ToolExecution.PrepareMealTool, ToolKind.Write, new { description }, async () =>
        {
            var parsed = await _parser.ParseAsync(description, ct);
            var proposal = await _nutrition.PrepareMealAsync(_userId, parsed, ct);
            var json = JsonSerializer.Serialize(proposal, ProposalJson.Options);
            return new ToolResult(
                new
                {
                    status = "proposal_shown_to_user",
                    saved = false,
                    note = "The user must confirm in the card. Do not say the meal was saved. Report only these numbers.",
                    mealType = proposal.MealType,
                    items = proposal.Items.Select(i => new
                    {
                        input = i.InputText,
                        i.Status,
                        food = i.Food?.Description,
                        grams = i.QuantityGrams,
                        calories = i.Nutrients.Calories,
                        i.Note,
                    }),
                    totals = proposal.Totals,
                    warnings = proposal.Warnings,
                },
                ToolOutcome.Proposed,
                json,
                id => PendingEvents.Enqueue(new ChatStreamEvent.Proposal(
                    new ProposalDto(id, _assistantSequence, ToolOutcome.Proposed.ToString(), null, proposal))));
        });

    // ---- plumbing ----

    private sealed record ToolResult(object ModelResult, ToolOutcome Outcome, string? ResultJson, Action<Guid>? AfterPersist);

    private static ToolResult Result(object value) => new(value, ToolOutcome.Succeeded, null, null);

    private static DateOnly ParseDate(string value) =>
        DateOnly.TryParseExact(value?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw new BadRequestException("invalid_date", "Dates must be written as YYYY-MM-DD.");

    private async Task<object> RunAsync(string tool, ToolKind kind, object arguments, Func<Task<ToolResult>> body)
    {
        var started = Stopwatch.GetTimestamp();
        object modelResult;
        var outcome = ToolOutcome.Succeeded;
        string? resultJson = null;
        string? errorCode = null;
        Action<Guid>? afterPersist = null;

        try
        {
            var result = await body();
            (modelResult, outcome, resultJson, afterPersist) = (result.ModelResult, result.Outcome, result.ResultJson, result.AfterPersist);
        }
        catch (AppException ex)
        {
            outcome = ex.StatusCode >= 500 ? ToolOutcome.Failed : ToolOutcome.Rejected;
            errorCode = ex.Code;
            modelResult = new { error = ex.Message, code = ex.Code };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Tool {Tool} failed: {ExceptionType}", tool, ex.GetType().Name);
            outcome = ToolOutcome.Failed;
            errorCode = "tool_error";
            modelResult = new { error = "The tool failed. Tell the user you could not retrieve this information.", code = "tool_error" };
        }

        var execution = new ToolExecution(
            _conversationId, _userId, _assistantSequence, tool, kind, JsonSerializer.Serialize(arguments, ProposalJson.Options),
            outcome, resultJson, errorCode, (int)TimeProvider.System.GetElapsedTime(started).TotalMilliseconds, _clock.GetUtcNow().UtcDateTime);
        _db.Add(execution);
        await _db.SaveChangesAsync(CancellationToken.None);

        if (afterPersist is not null)
        {
            ProposalCount++;
            afterPersist(execution.Id);
        }

        return modelResult;
    }
}
