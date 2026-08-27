using System.Globalization;
using System.Text;

namespace SessionSearch.Core.Text;

public static class TextNormalization
{
    public const int DescriptionMaxScalars = 180;
    public const int DescriptionPrefixScalars = 177;

    public static string NormalizeDisplay(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        string normalized = value.Normalize(NormalizationForm.FormKC);
        StringBuilder result = new(normalized.Length);
        bool pendingSpace = false;

        foreach (Rune rune in normalized.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = result.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                result.Append(' ');
                pendingSpace = false;
            }

            result.Append(rune.ToString());
        }

        return result.ToString();
    }

    public static string NormalizeMetadata(string? value) =>
        NormalizeDisplay(value).ToUpperInvariant();

    public static string TruncateDescription(string value)
    {
        Rune[] runes = value.EnumerateRunes().ToArray();
        if (runes.Length <= DescriptionMaxScalars)
        {
            return value;
        }

        int lastWhitespace = -1;
        for (int index = 0; index < DescriptionPrefixScalars; index++)
        {
            if (Rune.IsWhiteSpace(runes[index]))
            {
                lastWhitespace = index;
            }
        }

        int scalarCount = lastWhitespace >= 0 ? lastWhitespace : DescriptionPrefixScalars;
        StringBuilder result = new();
        for (int index = 0; index < scalarCount; index++)
        {
            result.Append(runes[index].ToString());
        }

        return result.ToString().TrimEnd() + "...";
    }
}

public static class DisplayTextSanitizer
{
    private const int ReplacementCharacter = 0xFFFD;

    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        StringBuilder safe = new(value.Length);
        foreach (Rune rune in value.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            if (IsLineOrTab(rune))
            {
                safe.Append(' ');
            }
            else if (IsUnsafeControl(rune))
            {
                safe.Append(char.ConvertFromUtf32(ReplacementCharacter));
            }
            else
            {
                safe.Append(rune.ToString());
            }
        }

        return TextNormalization.NormalizeDisplay(safe.ToString());
    }

    private static bool IsLineOrTab(Rune rune) =>
        rune.Value is '\t' or '\n' or '\r';

    private static bool IsUnsafeControl(Rune rune)
    {
        int value = rune.Value;
        return value is >= 0 and <= 0x1F
            || value is >= 0x7F and <= 0x9F
            || value is >= 0x202A and <= 0x202E
            || value is >= 0x2066 and <= 0x2069;
    }
}
