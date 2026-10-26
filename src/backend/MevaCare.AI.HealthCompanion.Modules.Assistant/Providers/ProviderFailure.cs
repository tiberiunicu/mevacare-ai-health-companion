using System.ClientModel;

namespace MevaCare.AI.HealthCompanion.Modules.Assistant.Providers;

/// <summary>
/// What may be logged about a failed provider call: the exception type and, for HTTP failures, the status code (401 = key, 404 = endpoint or
/// deployment name, 400 = unsupported parameter, 429 = quota). Never the message, URL, headers or body, which can contain keys or user text.
/// </summary>
public static class ProviderFailure
{
    /// <summary>The provider's own safety filter (for example Azure OpenAI content management) refused the prompt. The user can rephrase; it is not an outage.</summary>
    public static bool IsContentFilter(Exception exception) => exception.Message.Contains("content_filter", StringComparison.OrdinalIgnoreCase);

    public static string Describe(Exception exception, bool includeDetails = false)
    {
        var text = exception is ClientResultException { Status: > 0 } http ? $"{exception.GetType().Name} (HTTP {http.Status})" : exception.GetType().Name;
        if (includeDetails)
        {
            var message = exception.Message.ReplaceLineEndings(" ");
            text += ": " + (message.Length <= 400 ? message : message[..400] + "...");
        }

        return text;
    }
}
