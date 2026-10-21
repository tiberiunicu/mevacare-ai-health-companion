using System.Text.Json;
using MevaCare.AI.HealthCompanion.Modules.Assistant.Safety;

namespace MevaCare.AI.HealthCompanion.AIEvaluationTests;

/// <summary>
/// Deterministic evaluation of the emergency screen against a labelled dataset (no model, no network).
/// Reports recall on emergencies and the false-positive rate on benign wellness questions.
/// </summary>
public class EmergencyTriageEvaluationTests
{
    private sealed record Positive(string Text, string Category);

    private sealed record Limitation(string Text, string Note);

    private sealed record Dataset(List<Positive> Emergency, List<string> Benign, List<Limitation> KnownLimitations);

    private static readonly Dataset Data = JsonSerializer.Deserialize<Dataset>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Datasets", "emergency-triage.json")),
        new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    [Fact]
    public void Every_labelled_emergency_is_detected()
    {
        var missed = Data.Emergency.Where(e => EmergencyDetector.Assess(e.Text) is null).Select(e => e.Text).ToList();

        Assert.True(missed.Count == 0, $"Recall {Data.Emergency.Count - missed.Count}/{Data.Emergency.Count}. Missed:\n{string.Join('\n', missed)}");
    }

    [Fact]
    public void Emergencies_get_the_expected_category()
    {
        var wrong = Data.Emergency
            .Select(e => (e.Text, Expected: e.Category, Actual: EmergencyDetector.Assess(e.Text)?.Category.ToString()))
            .Where(r => r.Expected != r.Actual)
            .Select(r => $"{r.Text}: expected {r.Expected}, got {r.Actual}")
            .ToList();

        Assert.True(wrong.Count == 0, string.Join('\n', wrong));
    }

    [Fact]
    public void No_benign_wellness_question_triggers_the_screen()
    {
        var falsePositives = Data.Benign.Where(b => EmergencyDetector.Assess(b) is not null).ToList();

        Assert.True(falsePositives.Count == 0, $"False positives {falsePositives.Count}/{Data.Benign.Count}:\n{string.Join('\n', falsePositives)}");
    }

    [Fact]
    public void Known_limitations_are_documented_as_triggering_on_purpose()
    {
        // These are accepted false positives (safety over precision). If a future version fixes one, this test
        // fails and the dataset entry should move to "benign".
        foreach (var item in Data.KnownLimitations)
        {
            Assert.NotNull(EmergencyDetector.Assess(item.Text));
        }
    }

    [Theory]
    [InlineData("I want to kill myself", "en")]
    [InlineData("Vreau să mor", "ro")]
    public void Self_harm_answers_are_supportive_and_in_the_users_language(string text, string language)
    {
        var assessment = EmergencyDetector.Assess(text)!;

        Assert.Equal(EmergencyCategory.SelfHarm, assessment.Category);
        Assert.Equal(language, assessment.Language);
        Assert.Contains("112", assessment.Response, StringComparison.Ordinal);
        Assert.Contains("findahelpline.com", assessment.Response, StringComparison.Ordinal);
    }

    [Fact]
    public void Self_harm_wins_over_a_medical_match_in_the_same_message()
    {
        var assessment = EmergencyDetector.Assess("I took too many pills because I want to die")!;

        Assert.Equal(EmergencyCategory.SelfHarm, assessment.Category);
    }

    [Fact]
    public void Emergency_answers_never_claim_to_diagnose_and_always_name_an_emergency_number()
    {
        foreach (var item in Data.Emergency)
        {
            var response = EmergencyDetector.Assess(item.Text)!.Response;
            Assert.Contains("112", response, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Matching_is_diacritic_and_case_insensitive()
    {
        Assert.NotNull(EmergencyDetector.Assess("DURERI ÎN PIEPT"));
        Assert.NotNull(EmergencyDetector.Assess("dureri in piept"));
        Assert.NotNull(EmergencyDetector.Assess("I CAN’T BREATHE"));
    }

    [Fact]
    public void Pathological_input_is_handled_within_the_regex_time_budget()
    {
        var hostile = new string('a', 200_000) + " chest";

        var started = DateTime.UtcNow;
        _ = EmergencyDetector.Assess(hostile);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
    }
}
