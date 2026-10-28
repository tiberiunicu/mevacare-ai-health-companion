using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Text.Json;
using MevaCare.AI.HealthCompanion.Contracts.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.OpenFoodFacts;

/// <summary>Settings under <c>Nutrition:OpenFoodFacts</c>: the optional online lookup of packaged products.</summary>
public sealed class OpenFoodFactsOptions
{
    public const string SectionName = "Nutrition:OpenFoodFacts";

    /// <summary>When false the online lookup answers 503 <c>food_online_disabled</c> and nothing leaves the server.</summary>
    public bool Enabled { get; init; } = true;

    [Required, Url] public string BaseUrl { get; init; } = "https://world.openfoodfacts.org";

    /// <summary>Appended to the User-Agent so Open Food Facts can contact the operator (their terms ask for an identifying User-Agent). Optional.</summary>
    public string ContactEmail { get; init; } = "";

    [Range(1, 30)] public int TimeoutSeconds { get; init; } = 10;

    [Range(1, 20)] public int MaxResults { get; init; } = 10;

    /// <summary>
    /// Search requests per minute for the whole server. Open Food Facts documents 10 per minute and IP address and bans abusive clients,
    /// so the default stays below it; the limit is shared by all users.
    /// </summary>
    [Range(1, 10)] public int SearchesPerMinute { get; init; } = 8;

    /// <summary>Product (barcode) reads per minute for the whole server; Open Food Facts documents 100.</summary>
    [Range(1, 100)] public int ProductReadsPerMinute { get; init; } = 60;
}

public interface IOpenFoodFactsClient
{
    Task<IReadOnlyList<OffProduct>> SearchAsync(string text, CancellationToken ct);

    Task<OffProduct?> GetProductAsync(string barcode, CancellationToken ct);
}

/// <summary>
/// Reads Open Food Facts over HTTPS (read-only, no credentials, no user data: only the search text or a barcode is sent). Failures, timeouts and
/// unreadable answers become <c>food_online_unavailable</c> (HTTP 503); the provider's text, URLs and headers never reach the client.
/// </summary>
public sealed class OpenFoodFactsClient(HttpClient http, IOptions<OpenFoodFactsOptions> options, SlidingRateGate gate, ILogger<OpenFoodFactsClient> logger)
    : IOpenFoodFactsClient
{
    private const string Fields = "code,product_name,brands,categories_tags,quantity,serving_size,serving_quantity,nutriments";
    private readonly OpenFoodFactsOptions _options = options.Value;

    public async Task<IReadOnlyList<OffProduct>> SearchAsync(string text, CancellationToken ct)
    {
        EnsureEnabled();
        gate.Acquire("search", _options.SearchesPerMinute);
        var url = $"cgi/search.pl?search_terms={Uri.EscapeDataString(text)}&search_simple=1&action=process&json=1&page_size={_options.MaxResults}&fields={Fields}";
        using var document = await GetJsonAsync(url, ct);
        if (!document.RootElement.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return products.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object).Select(ParseProduct).ToList();
    }

    public async Task<OffProduct?> GetProductAsync(string barcode, CancellationToken ct)
    {
        EnsureEnabled();
        gate.Acquire("product", _options.ProductReadsPerMinute);
        using var document = await GetJsonAsync($"api/v2/product/{Uri.EscapeDataString(barcode)}.json?fields={Fields}", ct, notFoundIsEmpty: true);
        return document.RootElement.TryGetProperty("product", out var product) && product.ValueKind == JsonValueKind.Object ? ParseProduct(product) : null;
    }

    private void EnsureEnabled()
    {
        if (!_options.Enabled)
        {
            throw new ServiceUnavailableException("food_online_disabled", "The online food lookup is turned off on this server.");
        }
    }

    private async Task<JsonDocument> GetJsonAsync(string relativeUrl, CancellationToken ct, bool notFoundIsEmpty = false)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
            using var response = await http.GetAsync(relativeUrl, timeout.Token);
            if (notFoundIsEmpty && response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return JsonDocument.Parse("{}");
            }

            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Type and status only: the message may contain the URL and therefore the search text.
            logger.LogWarning("Open Food Facts request failed: {Failure}", ex is HttpRequestException { StatusCode: { } status } ? $"HTTP {(int)status}" : ex.GetType().Name);
            throw new ServiceUnavailableException("food_online_unavailable", "The online food database is not available right now.");
        }
    }

    internal static OffProduct ParseProduct(JsonElement product)
    {
        var nutriments = product.TryGetProperty("nutriments", out var n) && n.ValueKind == JsonValueKind.Object ? n : default;
        return new OffProduct(
            Code: Text(product, "code"),
            ProductName: Text(product, "product_name"),
            Brands: Text(product, "brands"),
            CategoryTag: Text(product, "categories_tags"),
            Quantity: Text(product, "quantity"),
            ServingSize: Text(product, "serving_size"),
            ServingQuantityG: Number(product, "serving_quantity"),
            EnergyKcal: Number(nutriments, "energy-kcal_100g"),
            EnergyKj: Number(nutriments, "energy_100g"),
            Protein: Number(nutriments, "proteins_100g"),
            Carbohydrates: Number(nutriments, "carbohydrates_100g"),
            Fat: Number(nutriments, "fat_100g"),
            Fiber: Number(nutriments, "fiber_100g"));
    }

    /// <summary>A string, or a list of strings joined by commas (brands and category tags come in both shapes).</summary>
    private static string? Text(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.Array => string.Join(',', value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString())),
            _ => null,
        };
    }

    /// <summary>A number or a numeric string; anything else is "unknown".</summary>
    private static decimal? Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
        {
            return null;
        }

        try
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.TryGetDecimal(out var d) ? d : null,
                JsonValueKind.String => decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
                _ => null,
            };
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// A server-wide limit of calls per minute, shared by all users, so the application as a whole stays inside the limits Open Food Facts publishes.
/// Over the limit, callers get HTTP 429 <c>rate_limited</c> instead of waiting.
/// </summary>
public sealed class SlidingRateGate(TimeProvider clock)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _calls = [];

    public void Acquire(string key, int perMinute)
    {
        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (!_calls.TryGetValue(key, out var queue))
            {
                _calls[key] = queue = new Queue<DateTimeOffset>();
            }

            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromMinutes(1))
            {
                queue.Dequeue();
            }

            if (queue.Count >= perMinute)
            {
                throw new RateLimitedException("The online food lookup is busy; try again in a minute.");
            }

            queue.Enqueue(now);
        }
    }
}
