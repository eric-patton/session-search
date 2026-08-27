using System.Globalization;
using System.Text;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Text;

namespace SessionSearch.Core.Search;

public static class QueryParser
{
    public static QueryParseResult Parse(string? text)
    {
        text ??= string.Empty;

        if (text.Contains('\0', StringComparison.Ordinal))
        {
            return QueryParseResult.Failure(
                QueryErrorCode.ContainsNul,
                "The query contains a NUL character.");
        }

        if (text.Contains(ProviderLimits.SearchRecordBoundaryToken, StringComparison.Ordinal))
        {
            return QueryParseResult.Failure(
                QueryErrorCode.ContainsReservedBoundary,
                "The query contains a reserved index boundary character.");
        }

        if (text.EnumerateRunes().Count() > QueryLimits.MaxScalars)
        {
            return QueryParseResult.Failure(
                QueryErrorCode.TooManyScalars,
                $"The query exceeds {QueryLimits.MaxScalars} Unicode characters.");
        }

        List<RawAtom> rawAtoms = ParseAtoms(text);
        if (rawAtoms.Count > QueryLimits.MaxAtoms)
        {
            return QueryParseResult.Failure(
                QueryErrorCode.TooManyAtoms,
                $"The query exceeds {QueryLimits.MaxAtoms} search atoms.");
        }

        List<QueryAtom> atoms = new(rawAtoms.Count);
        int transcriptTokenCount = 0;
        foreach (RawAtom rawAtom in rawAtoms)
        {
            string display = TextNormalization.NormalizeDisplay(rawAtom.Text);
            if (display.Length == 0)
            {
                continue;
            }

            List<string> transcriptTokens = TokenizeForFts(display);
            transcriptTokenCount += transcriptTokens.Count;
            if (transcriptTokenCount > QueryLimits.MaxTranscriptTokens)
            {
                return QueryParseResult.Failure(
                    QueryErrorCode.TooManyTranscriptTokens,
                    $"The query exceeds {QueryLimits.MaxTranscriptTokens} transcript tokens.");
            }

            string transcriptExpression = BuildAtomFtsExpression(
                rawAtom.Kind,
                transcriptTokens);
            atoms.Add(new QueryAtom(
                rawAtom.Kind,
                display,
                TextNormalization.NormalizeMetadata(display),
                transcriptTokens,
                transcriptExpression));
        }

        if (atoms.Count > QueryLimits.MaxAtoms)
        {
            return QueryParseResult.Failure(
                QueryErrorCode.TooManyAtoms,
                $"The query exceeds {QueryLimits.MaxAtoms} search atoms.");
        }

        string ftsExpression = BuildFtsExpression(atoms);
        if (ftsExpression.Length > QueryLimits.MaxFtsExpressionCharacters)
        {
            return QueryParseResult.Failure(
                QueryErrorCode.FtsExpressionTooLong,
                $"The generated transcript query exceeds {QueryLimits.MaxFtsExpressionCharacters} characters.");
        }

        string normalizedText = string.Join(' ', atoms.Select(atom => atom.NormalizedText));
        return QueryParseResult.Success(new ParsedQuery(
            text,
            normalizedText,
            atoms,
            ftsExpression));
    }

    private static List<RawAtom> ParseAtoms(string text)
    {
        List<RawAtom> atoms = [];
        StringBuilder unquoted = new();
        int index = 0;

        while (index < text.Length)
        {
            if (text[index] != '"')
            {
                unquoted.Append(text[index]);
                index++;
                continue;
            }

            int closingQuote = text.IndexOf('"', index + 1);
            if (closingQuote < 0)
            {
                AddTerms(unquoted, atoms);
                unquoted.Clear();
                index++;
                continue;
            }

            AddTerms(unquoted, atoms);
            unquoted.Clear();

            string phrase = text[(index + 1)..closingQuote];
            if (!string.IsNullOrWhiteSpace(phrase))
            {
                atoms.Add(new RawAtom(QueryAtomKind.Phrase, phrase));
            }

            index = closingQuote + 1;
        }

        AddTerms(unquoted, atoms);
        return atoms;
    }

    private static void AddTerms(StringBuilder value, ICollection<RawAtom> atoms)
    {
        int tokenStart = -1;
        for (int index = 0; index <= value.Length; index++)
        {
            bool isBoundary = index == value.Length || char.IsWhiteSpace(value[index]);
            if (!isBoundary && tokenStart < 0)
            {
                tokenStart = index;
            }

            if (isBoundary && tokenStart >= 0)
            {
                atoms.Add(new RawAtom(
                    QueryAtomKind.Term,
                    value.ToString(tokenStart, index - tokenStart)));
                tokenStart = -1;
            }
        }
    }

    private static List<string> TokenizeForFts(string value)
    {
        List<string> tokens = [];
        StringBuilder current = new();

        foreach (Rune rune in value.Normalize(NormalizationForm.FormKC).EnumerateRunes())
        {
            UnicodeCategory category = Rune.GetUnicodeCategory(rune);
            bool isToken = category is UnicodeCategory.UppercaseLetter
                or UnicodeCategory.LowercaseLetter
                or UnicodeCategory.TitlecaseLetter
                or UnicodeCategory.ModifierLetter
                or UnicodeCategory.OtherLetter
                or UnicodeCategory.DecimalDigitNumber
                or UnicodeCategory.LetterNumber
                or UnicodeCategory.OtherNumber
                or UnicodeCategory.PrivateUse;
            bool isCombiningMark = category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.EnclosingMark;

            if (isToken)
            {
                current.Append(rune.ToString());
            }
            else if (isCombiningMark && current.Length > 0)
            {
                current.Append(rune.ToString());
            }
            else if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    private static string BuildFtsExpression(IEnumerable<QueryAtom> atoms)
    {
        return string.Join(
            " AND ",
            atoms
                .Select(atom => atom.TranscriptExpression)
                .Where(expression => expression.Length > 0));
    }

    private static string BuildAtomFtsExpression(
        QueryAtomKind kind,
        List<string> transcriptTokens)
    {
        if (transcriptTokens.Count == 0)
        {
            return string.Empty;
        }

        if (kind == QueryAtomKind.Phrase)
        {
            return QuoteLiteral(string.Join(' ', transcriptTokens));
        }

        List<string> tokenExpressions = [];
        for (int index = 0; index < transcriptTokens.Count; index++)
        {
            string token = QuoteLiteral(transcriptTokens[index]);
            if (index == transcriptTokens.Count - 1)
            {
                token += '*';
            }

            tokenExpressions.Add(token);
        }

        return string.Join(" AND ", tokenExpressions);
    }

    private static string QuoteLiteral(string value) =>
        $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private sealed record RawAtom(QueryAtomKind Kind, string Text);
}
