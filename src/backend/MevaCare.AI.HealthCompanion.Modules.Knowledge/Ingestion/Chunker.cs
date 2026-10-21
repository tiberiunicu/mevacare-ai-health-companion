using System.Text.RegularExpressions;

namespace MevaCare.AI.HealthCompanion.Modules.Knowledge.Ingestion;

public sealed record ChunkDraft(int Ordinal, string? Heading, string Text);

/// <summary>
/// Splits extracted sections into chunks of roughly <see cref="ChunkingOptions.TargetCharacters"/>. Chunks never cross a heading,
/// never end in the middle of a sentence (unless one sentence alone exceeds the limit), and repeat the last sentence(s) of the previous chunk
/// as a small overlap so a fact that straddles a boundary is still found. The heading is stored with the chunk and embedded with it.
/// </summary>
public sealed partial class Chunker(ChunkingOptions options)
{
    private const int MinTrailingCharacters = 200;

    public IReadOnlyList<ChunkDraft> Chunk(ExtractedDocument document)
    {
        var chunks = new List<ChunkDraft>();
        foreach (var section in document.Sections)
        {
            foreach (var text in ChunkSection(section))
            {
                chunks.Add(new ChunkDraft(chunks.Count, section.Heading, text));
            }
        }

        return chunks;
    }

    private List<string> ChunkSection(DocumentSection section)
    {
        // Units are paragraphs, or sentences when a paragraph alone is too long. A unit is never split further.
        var units = new List<string>();
        foreach (var paragraph in section.Paragraphs)
        {
            if (paragraph.Length <= options.MaxCharacters)
            {
                units.Add(paragraph);
            }
            else
            {
                units.AddRange(Sentences(paragraph));
            }
        }

        var result = new List<string>();
        var current = new List<string>();
        var carried = 0; // leading units of `current` that are only the overlap copied from the previous chunk
        var length = 0;

        foreach (var unit in units)
        {
            if (current.Count > carried && length + unit.Length + 1 > options.TargetCharacters)
            {
                result.Add(string.Join('\n', current));
                var overlap = TrailingOverlap(current);
                current = overlap is null ? [] : [overlap];
                carried = current.Count;
                length = overlap?.Length + 1 ?? 0;
            }

            current.Add(unit);
            length += unit.Length + 1;
        }

        if (current.Count > carried)
        {
            result.Add(string.Join('\n', current));
        }

        // A tiny last chunk is merged into the previous one when that stays within the limit.
        if (result.Count > 1 && result[^1].Length < MinTrailingCharacters && result[^2].Length + result[^1].Length + 1 <= options.MaxCharacters)
        {
            result[^2] = result[^2] + '\n' + result[^1];
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    /// <summary>
    /// The last whole sentences of the chunk, up to the overlap size, or null. Walks backwards over the chunk's units (taking only the
    /// trailing sentences of a unit that does not fit entirely) and never returns the whole chunk.
    /// </summary>
    private string? TrailingOverlap(List<string> chunkUnits)
    {
        if (options.OverlapCharacters <= 0)
        {
            return null;
        }

        var taken = new List<string>();
        var total = 0;
        var stop = false;
        for (var i = chunkUnits.Count - 1; i >= 0 && !stop; i--)
        {
            var sentences = Sentences(chunkUnits[i]);
            for (var j = sentences.Count - 1; j >= 0; j--)
            {
                if (total + sentences[j].Length > options.OverlapCharacters)
                {
                    stop = true;
                    break;
                }

                taken.Insert(0, sentences[j]);
                total += sentences[j].Length + 1;
            }
        }

        var text = string.Join(" ", taken);
        return taken.Count == 0 || text.Length >= chunkUnits.Sum(u => u.Length) ? null : text;
    }

    internal static List<string> Sentences(string text) =>
        SentenceBoundary().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    [GeneratedRegex(@"(?<=[.!?])\s+(?=[A-Z0-9""'(\[])")]
    private static partial Regex SentenceBoundary();
}
