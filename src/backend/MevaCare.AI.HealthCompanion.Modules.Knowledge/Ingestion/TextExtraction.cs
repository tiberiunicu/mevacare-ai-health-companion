using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace MevaCare.AI.HealthCompanion.Modules.Knowledge.Ingestion;

/// <summary>A heading and the paragraphs under it. The heading path ("Vitamin D > Sources") is kept for citations and embedding.</summary>
public sealed record DocumentSection(string? Heading, IReadOnlyList<string> Paragraphs);

public sealed record ExtractedDocument(string? Title, IReadOnlyList<DocumentSection> Sections)
{
    public string PlainText => string.Join("\n\n", Sections.SelectMany(s => s.Paragraphs));
}

/// <summary>Turns a raw document into clean text sections. Pure and offline: no network, no model.</summary>
public interface IDocumentTextExtractor
{
    ExtractedDocument Extract(string content);
}

public static class DocumentExtractors
{
    public static readonly IReadOnlySet<string> SupportedMediaTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "text/html", "text/markdown", "text/plain",
    };

    /// <summary>PDF and Office formats are not supported yet (see docs/ai/rag-embeddings-memory.md).</summary>
    public static IDocumentTextExtractor For(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "text/html" => new HtmlTextExtractor(),
        "text/markdown" => new MarkdownTextExtractor(),
        "text/plain" => new PlainTextExtractor(),
        _ => throw new NotSupportedException($"Media type '{mediaType}' is not supported. Supported: text/html, text/markdown, text/plain."),
    };

    public static string MediaTypeFromExtension(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".md" or ".markdown" => "text/markdown",
        ".txt" => "text/plain",
        var other => throw new NotSupportedException($"File type '{other}' is not supported."),
    };

    internal static string Normalize(string text) =>
        Regex.Replace(WebUtility.HtmlDecode(text).Replace(' ', ' '), @"[ \t\r\n\f\v]+", " ").Trim();
}

public sealed class PlainTextExtractor : IDocumentTextExtractor
{
    public ExtractedDocument Extract(string content)
    {
        var paragraphs = Regex.Split(content.Replace("\r\n", "\n"), @"\n\s*\n")
            .Select(DocumentExtractors.Normalize).Where(p => p.Length > 0).ToList();
        return new ExtractedDocument(null, paragraphs.Count == 0 ? [] : [new DocumentSection(null, paragraphs)]);
    }
}

public sealed class MarkdownTextExtractor : IDocumentTextExtractor
{
    public ExtractedDocument Extract(string content)
    {
        var sections = new List<DocumentSection>();
        var headings = new List<(int Level, string Text)>();
        var paragraphs = new List<string>();
        var buffer = new StringBuilder();
        string? title = null;

        void FlushParagraph()
        {
            var text = DocumentExtractors.Normalize(buffer.ToString());
            buffer.Clear();
            if (text.Length > 0)
            {
                paragraphs.Add(text);
            }
        }

        void FlushSection()
        {
            FlushParagraph();
            if (paragraphs.Count > 0)
            {
                sections.Add(new DocumentSection(HeadingPath(headings), paragraphs.ToList()));
                paragraphs.Clear();
            }
        }

        foreach (var rawLine in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = rawLine.TrimEnd();
            var heading = Regex.Match(line, @"^(#{1,6})\s+(.+?)\s*#*$");
            if (heading.Success)
            {
                FlushSection();
                var level = heading.Groups[1].Length;
                headings.RemoveAll(h => h.Level >= level);
                headings.Add((level, heading.Groups[2].Value.Trim()));
                title ??= heading.Groups[2].Value.Trim();
            }
            else if (line.Length == 0)
            {
                FlushParagraph();
            }
            else if (Regex.IsMatch(line, @"^\s*([-*+]|\d+[.)])\s+"))
            {
                FlushParagraph();
                buffer.Append(Regex.Replace(line, @"^\s*([-*+]|\d+[.)])\s+", ""));
                FlushParagraph();
            }
            else
            {
                buffer.Append(line).Append(' ');
            }
        }

        FlushSection();
        return new ExtractedDocument(title, sections);
    }

    internal static string? HeadingPath(List<(int Level, string Text)> headings) =>
        headings.Count == 0 ? null : string.Join(" > ", headings.Select(h => h.Text));
}

/// <summary>
/// Extracts readable text from an HTML page: drops navigation, scripts, forms and other page chrome, prefers the main
/// content region, and keeps the heading hierarchy. Web pages are messy; the result must always be reviewed by whoever approves a source.
/// </summary>
public sealed class HtmlTextExtractor : IDocumentTextExtractor
{
    private static readonly string[] RemovedTags =
        ["script", "style", "noscript", "nav", "header", "footer", "aside", "form", "svg", "iframe", "button", "select", "template", "dialog"];

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
        { "p", "li", "dd", "dt", "blockquote", "pre", "td", "th", "figcaption" };

    private static readonly HashSet<string> Containers = new(StringComparer.OrdinalIgnoreCase)
        { "ul", "ol", "dl", "table", "tbody", "thead", "tr", "div", "section", "article", "main", "span", "details", "summary", "body", "figure" };

    private static readonly Regex ChromeClass = new(
        @"(^|[\s_-])(breadcrumb|cookie|social|share|skip|sidebar|toolbar|pager|pagination|related|feedback|print|screen-reader|sr-only|visually-hidden)([\s_-]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public ExtractedDocument Extract(string content)
    {
        var html = new HtmlDocument();
        html.LoadHtml(content);

        var title = DocumentExtractors.Normalize(html.DocumentNode.SelectSingleNode("//title")?.InnerText ?? "");
        foreach (var tag in RemovedTags)
        {
            foreach (var node in html.DocumentNode.Descendants(tag).ToList())
            {
                node.Remove();
            }
        }

        foreach (var node in html.DocumentNode.Descendants().Where(IsChrome).ToList())
        {
            node.Remove();
        }

        var root = html.DocumentNode.SelectSingleNode("//main")
            ?? html.DocumentNode.SelectSingleNode("//*[@role='main']")
            ?? html.DocumentNode.SelectSingleNode("//article")
            ?? html.DocumentNode.SelectSingleNode("//body")
            ?? html.DocumentNode;

        var sections = new List<DocumentSection>();
        var headings = new List<(int Level, string Text)>();
        var paragraphs = new List<string>();
        string? firstHeading = null;

        void FlushSection()
        {
            if (paragraphs.Count > 0)
            {
                sections.Add(new DocumentSection(MarkdownTextExtractor.HeadingPath(headings), paragraphs.ToList()));
                paragraphs.Clear();
            }
        }

        void Visit(HtmlNode node)
        {
            foreach (var child in node.ChildNodes)
            {
                if (child.NodeType != HtmlNodeType.Element)
                {
                    continue;
                }

                var name = child.Name.ToLowerInvariant();
                if (name.Length == 2 && name[0] == 'h' && char.IsDigit(name[1]) && name[1] is >= '1' and <= '6')
                {
                    var text = DocumentExtractors.Normalize(child.InnerText);
                    if (text.Length > 0)
                    {
                        FlushSection();
                        var level = name[1] - '0';
                        headings.RemoveAll(h => h.Level >= level);
                        headings.Add((level, text));
                        firstHeading ??= text;
                    }
                }
                else if (BlockTags.Contains(name) && !child.Descendants().Any(d => BlockTags.Contains(d.Name)))
                {
                    var text = DocumentExtractors.Normalize(child.InnerText);
                    if (text.Length >= (name == "li" ? 8 : 20) && !IsLinkList(child, text))
                    {
                        paragraphs.Add(text);
                    }
                }
                else if (BlockTags.Contains(name) || Containers.Contains(name) || child.HasChildNodes)
                {
                    Visit(child);
                }
            }
        }

        Visit(root);
        FlushSection();

        // Pages repeat boilerplate lines; keep each paragraph once.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var deduplicated = sections
            .Select(s => new DocumentSection(s.Heading, s.Paragraphs.Where(seen.Add).ToList()))
            .Where(s => s.Paragraphs.Count > 0)
            .ToList();

        return new ExtractedDocument(title.Length > 0 ? title : firstHeading, deduplicated);
    }

    /// <summary>
    /// Tab bars, "related links" and reference lists are made of links, not statements. An element whose text is (almost) all link text
    /// is page furniture, not knowledge: indexing it would only add noise that competes with real passages.
    /// </summary>
    private static bool IsLinkList(HtmlNode element, string text)
    {
        if (text.Equals("No links available", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var links = element.Descendants("a").ToList();
        var anchors = links.Sum(a => DocumentExtractors.Normalize(a.InnerText).Length);

        // Reference lists add "(Publisher)" and "Also in Spanish" around each link; those words are part of the furniture too.
        var residual = Regex.Replace(text, @"\([^)]*\)|Also in Spanish|In English and Spanish", "").Trim().Length;
        // "Related reading: <a>one</a> <a>two</a>": a short label around several links is still a link list.
        var shortLabelAroundLinks = links.Count >= 2 && text.Length - anchors <= 30;
        return anchors > 0 && (anchors >= Math.Max(residual, 1) * 0.8 || shortLabelAroundLinks);
    }

    private static bool IsChrome(HtmlNode node)
    {
        if (node.NodeType != HtmlNodeType.Element)
        {
            return false;
        }

        var role = node.GetAttributeValue("role", "");
        if (role is "navigation" or "banner" or "contentinfo" or "search" or "complementary")
        {
            return true;
        }

        if (node.GetAttributeValue("aria-hidden", "") == "true" || node.Attributes["hidden"] is not null)
        {
            return true;
        }

        var cls = node.GetAttributeValue("class", "");
        return cls.Length > 0 && ChromeClass.IsMatch(cls);
    }
}
