using System.Net;
using System.Text;

namespace MevaCare.AI.HealthCompanion.IntegrationTests.Support;

/// <summary>
/// A scripted stand-in for Open Food Facts that answers with the documented response shapes of its search and product endpoints.
/// NOT the real service: it proves how the application handles those shapes and failures, not that the real service answers identically.
/// </summary>
public sealed class FakeOpenFoodFactsHandler : HttpMessageHandler
{
    private readonly object _gate = new();

    public List<string> Requests { get; } = [];

    /// <summary>Product JSON objects by barcode (the shape of the <c>product</c> element of the API).</summary>
    public Dictionary<string, string> Products { get; } = [];

    public HttpStatusCode? FailWith { get; set; }

    public void Reset()
    {
        lock (_gate)
        {
            Requests.Clear();
            Products.Clear();
            FailWith = null;
        }
    }

    /// <summary>A cottage cheese. The brand is a parameter because the database is shared by the whole test collection: each test uses its own brand.</summary>
    public static string Hochland(string code = "5941238007810", string kcal = "95", string brand = "Hochland") =>
        $$$"""
        {"code":"{{{code}}}","product_name":"{{{brand}}} Cottage cheese","brands":"{{{brand}}}","quantity":"175 g,",
         "categories_tags":["en:dairies","en:cottage-cheeses"],
         "nutriments":{"energy-kcal_100g":{{{kcal}}},"proteins_100g":12,"carbohydrates_100g":2,"fat_100g":4.5,"fiber_100g":0}}
        """;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Requests.Add(request.RequestUri!.PathAndQuery);
            if (FailWith is { } status)
            {
                return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("provider internal detail") });
            }

            var path = request.RequestUri.AbsolutePath;
            if (path.EndsWith("/cgi/search.pl", StringComparison.Ordinal))
            {
                return Task.FromResult(Json($$"""{"count":{{Products.Count}},"products":[{{string.Join(',', Products.Values)}}]}"""));
            }

            const string productPrefix = "/api/v2/product/";
            if (path.StartsWith(productPrefix, StringComparison.Ordinal))
            {
                var code = path[productPrefix.Length..].Replace(".json", "", StringComparison.Ordinal);
                return Task.FromResult(Products.TryGetValue(code, out var product)
                    ? Json($$"""{"code":"{{code}}","status":1,"product":{{product}}}""")
                    : Json("""{"status":0}""", HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
