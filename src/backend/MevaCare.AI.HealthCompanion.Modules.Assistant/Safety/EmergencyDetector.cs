using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Safety;

public enum EmergencyCategory
{
    MedicalEmergency = 1,
    SelfHarm = 2,
}

public sealed record EmergencyAssessment(EmergencyCategory Category, string Language, string Response);

/// <summary>
/// Deterministic, model-independent screen for messages that describe an emergency in the first person.
/// When it fires, the assistant answers with a fixed, pre-written message and the LLM is NOT called, because
/// a safety guarantee must not depend on a probabilistic model.
/// Trade-off: it is a keyword heuristic. It is tuned for recall (a false alarm costs one unhelpful reply, a miss
/// can cost much more) and measured against a labelled dataset (tests/AIEvaluationTests). It is a safety net,
/// not a clinical triage tool, and it only understands English and Romanian.
/// </summary>
public static class EmergencyDetector
{
    private sealed record Rule(EmergencyCategory Category, string Language, Regex Pattern);

    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    private static Rule En(EmergencyCategory category, string pattern) => new(category, "en", new Regex(pattern, Options, MatchTimeout));
    private static Rule Ro(EmergencyCategory category, string pattern) => new(category, "ro", new Regex(pattern, Options, MatchTimeout));

    private static readonly Rule[] Rules =
    [
        // ---- English: medical ----
        En(EmergencyCategory.MedicalEmergency, @"\b(crushing|severe|sharp|tight|squeezing|sudden)\b.{0,20}\bchest (pain|pressure|tightness)\b"),
        En(EmergencyCategory.MedicalEmergency, @"\b(i have|i've got|i am having|i'm having|im having|i feel|my)\b.{0,15}\bchest (pain|pressure|tightness)\b"),
        En(EmergencyCategory.MedicalEmergency, @"\bchest (pain|pressure|tightness)\b.{0,40}\b(arm|jaw|sweating|short of breath|shortness of breath)\b"),
        En(EmergencyCategory.MedicalEmergency, @"\b(having|think i am having|think i'm having|might be having|may be having) a (heart attack|stroke)\b"),
        En(EmergencyCategory.MedicalEmergency, @"\b(can'?t|cannot|can not|unable to|not able to) (breathe|breath)\b"),
        En(EmergencyCategory.MedicalEmergency, @"\b(struggling|gasping) (to|for) (breathe|breath|air)\b"),
        En(EmergencyCategory.MedicalEmergency, @"\b(i have|i am having|i'm having|having) (severe |sudden )?(difficulty|trouble) breathing\b"),
        En(EmergencyCategory.MedicalEmergency, @"\b(face|mouth) (is )?(drooping|numb)\b|\bslurred speech\b|\bsudden(ly)? (numbness|weakness|confusion|loss of vision)\b"),
        En(EmergencyCategory.MedicalEmergency, @"\b(bleeding (heavily|badly|profusely)|won'?t stop bleeding|can'?t stop the bleeding)\b"),
        En(EmergencyCategory.MedicalEmergency, @"\boverdosed\b|\b(i|he|she|they|my \w+) (took|swallowed|ingested) (too many|too much|a whole bottle|an entire bottle)\b|\bpoisoned\b"),
        En(EmergencyCategory.MedicalEmergency, @"\b(throat|tongue) (is )?(closing|swelling)\b|\bswollen (throat|tongue)\b"),
        En(EmergencyCategory.MedicalEmergency, @"\b(having a seizure|is unconscious|is unresponsive|am unconscious|passed out|blacked out)\b"),

        // ---- English: self-harm ----
        En(EmergencyCategory.SelfHarm, @"\b(kill myself|killing myself|end(ing)? my (own )?life|tak(e|ing) my own life|want(ing)? to die|wanna die|suicidal|commit(ting)? suicide|hurt(ing)? myself|harm(ing)? myself|self[- ]harm(ing)?)\b"),
        En(EmergencyCategory.SelfHarm, @"\b(don'?t|do not) want to (live|be alive|be here anymore)\b|\bno reason to live\b"),

        // ---- Romanian (input is stripped of diacritics before matching) ----
        Ro(EmergencyCategory.MedicalEmergency, @"\bdurer\w* (\w+ )?in piept\b|\bma doare pieptul\b|\bpresiune in piept\b"),
        Ro(EmergencyCategory.MedicalEmergency, @"\b(cred ca (am|fac|face) (un )?infarct|fac infarct|am un infarct)\b"),
        Ro(EmergencyCategory.MedicalEmergency, @"\bnu pot (sa )?(respir\w*|rasufl\w*)\b|\bma sufoc\b|\bam dificultati (mari )?(de|la) respiratie\b"),
        Ro(EmergencyCategory.MedicalEmergency, @"\b(fata|gura) (stramba|cazuta)\b|\bvorbire (incurcata|neclara)\b|\bslabiciune brusca\b|\bparalizi\w+\b|\bcred ca (am|face) (un )?(avc|accident vascular)\b"),
        Ro(EmergencyCategory.MedicalEmergency, @"\bsangereaza (abundent|mult|puternic)\b|\bnu se opreste (sangele|sangerarea)\b"),
        Ro(EmergencyCategory.MedicalEmergency, @"\bsupradoz\w*\b|\bam luat prea multe (pastile|tablete|comprimate)\b|\b(m-?am otravit|a fost otravit)\b"),
        Ro(EmergencyCategory.MedicalEmergency, @"\bmi se (inchide|umfla) (gatul|limba)\b"),
        Ro(EmergencyCategory.MedicalEmergency, @"\b(are|face|am|fac) (o )?criza (convulsiva|de epilepsie)\b|\bconvulsii\b|\b(a|am) lesinat\b|\bnu raspunde la nimic\b"),

        Ro(EmergencyCategory.SelfHarm, @"\b(vreau sa mor|ma sinucid|sa ma sinucid|sinucid\w*|nu mai vreau sa traiesc|sa-?mi iau viata|sa imi iau viata|imi fac rau|sa ma tai)\b"),
    ];

    public static EmergencyAssessment? Assess(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var text = Normalize(message);
        EmergencyAssessment? medical = null;
        foreach (var rule in Rules)
        {
            bool matched;
            try
            {
                matched = rule.Pattern.IsMatch(text);
            }
            catch (RegexMatchTimeoutException)
            {
                // Fail safe: if a pattern cannot be evaluated in time, treat it as a match.
                matched = true;
            }

            if (!matched)
            {
                continue;
            }

            // Self-harm takes precedence over a generic medical emergency: the reply must be the supportive one.
            if (rule.Category == EmergencyCategory.SelfHarm)
            {
                return new EmergencyAssessment(rule.Category, rule.Language, SafetyMessages.For(rule.Category, rule.Language));
            }

            medical ??= new EmergencyAssessment(rule.Category, rule.Language, SafetyMessages.For(rule.Category, rule.Language));
        }

        return medical;
    }

    /// <summary>Lower-cases, unifies apostrophes and strips diacritics so "dureri în piept" matches "dureri in piept".</summary>
    internal static string Normalize(string input)
    {
        var decomposed = input.Replace('’', '\'').Replace('`', '\'').Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(c);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }
}

internal static class SafetyMessages
{
    public static string For(EmergencyCategory category, string language) => (category, language) switch
    {
        (EmergencyCategory.SelfHarm, "ro") =>
            "Îmi pare foarte rău că te simți așa. Meriți sprijin chiar acum și nu trebuie să treci singur/singură prin asta.\n\n" +
            "**Dacă ești în pericol sau crezi că ai putea să-ți faci rău, sună acum la numărul de urgență (112 în UE, 911 în SUA, 988 în SUA pentru linia de criză) sau mergi la cea mai apropiată cameră de gardă.**\n\n" +
            "Poți găsi o linie de ajutor din țara ta la https://findahelpline.com. Dacă poți, spune unei persoane de încredere cum te simți și rămâi lângă ea.\n\n" +
            "Sunt un asistent de wellness și nu pot oferi ajutor de urgență sau tratament, dar sunt aici să vorbim după ce ai asigurat siguranța.",
        (EmergencyCategory.SelfHarm, _) =>
            "I'm really sorry you're feeling this way. You deserve support right now, and you don't have to go through this alone.\n\n" +
            "**If you are in danger or might act on these thoughts, call your local emergency number now (112 in the EU, 911 in the US, or 988 for the US Suicide & Crisis Lifeline) or go to the nearest emergency department.**\n\n" +
            "You can find a helpline in your country at https://findahelpline.com. If you can, tell someone you trust how you feel and stay with them.\n\n" +
            "I'm a wellness assistant and cannot provide emergency care or treatment, but I'm here to talk once you are safe.",
        (_, "ro") =>
            "**Ce descrii ar putea fi o urgență medicală.** Nu pot evalua situația de aici.\n\n" +
            "**Sună acum la numărul de urgență (112 în UE, 911 în SUA) sau mergi la cea mai apropiată cameră de gardă.** Dacă ești cu cineva, roagă-l să rămână lângă tine.\n\n" +
            "Sunt un asistent de wellness: nu pun diagnostice și nu pot oferi îngrijiri de urgență.",
        _ =>
            "**What you describe could be a medical emergency.** I can't assess it from here.\n\n" +
            "**Please call your local emergency number now (112 in the EU, 911 in the US) or go to the nearest emergency department.** If you are with someone, ask them to stay with you.\n\n" +
            "I'm a wellness assistant: I can't diagnose or provide emergency care.",
    };
}
