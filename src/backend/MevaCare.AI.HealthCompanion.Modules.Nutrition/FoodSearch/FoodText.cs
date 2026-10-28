using System.Globalization;
using System.Text;

namespace MevaCare.AI.HealthCompanion.Modules.Nutrition.FoodSearch;

/// <summary>
/// Text normalisation shared by indexing and searching, so "brânză", "Branza" and "BRANZA" are the same word.
/// Foods are searched on a pre-folded column (<c>SearchText</c>) because the database collation is accent-sensitive.
/// </summary>
public static class FoodText
{
    public const int MaxSearchTextLength = 400;

    /// <summary>Lower-case, without diacritics (ă → a, ß → ss, ł → l); other characters are kept.</summary>
    public static string Fold(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            switch (ch)
            {
                case 'ß': builder.Append("ss"); break;
                case 'ł' or 'Ł': builder.Append('l'); break;
                case 'đ' or 'Đ': builder.Append('d'); break;
                case 'ø' or 'Ø': builder.Append('o'); break;
                case 'æ' or 'Æ': builder.Append("ae"); break;
                case 'œ' or 'Œ': builder.Append("oe"); break;
                default: builder.Append(char.ToLowerInvariant(ch)); break;
            }
        }

        return builder.ToString();
    }

    /// <summary>The text a food is found by: name, brand and barcode, folded and capped to the column size.</summary>
    public static string BuildSearchText(string description, string? brand, string? barcode)
    {
        var text = Fold(string.Join(' ', new[] { description, brand, barcode }.Where(s => !string.IsNullOrWhiteSpace(s))));
        return text.Length > MaxSearchTextLength ? text[..MaxSearchTextLength] : text;
    }

    /// <summary>True for a product barcode typed as the whole query (EAN-8, UPC-A, EAN-13, GTIN-14).</summary>
    public static bool IsBarcode(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length is 8 or 12 or 13 or 14 && trimmed.All(char.IsAsciiDigit);
    }
}
