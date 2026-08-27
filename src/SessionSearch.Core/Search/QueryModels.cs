namespace SessionSearch.Core.Search;

public static class QueryLimits
{
    public const int MaxScalars = 512;
    public const int MaxAtoms = 32;
    public const int MaxTranscriptTokens = 128;
    public const int MaxFtsExpressionCharacters = 4096;
}

public enum QueryAtomKind
{
    Term,
    Phrase,
}

public sealed record QueryAtom(
    QueryAtomKind Kind,
    string DisplayText,
    string NormalizedText,
    IReadOnlyList<string> TranscriptTokens,
    string TranscriptExpression);

public sealed record ParsedQuery(
    string OriginalText,
    string NormalizedText,
    IReadOnlyList<QueryAtom> Atoms,
    string FtsExpression)
{
    public bool IsBrowse => Atoms.Count == 0;
}

public enum QueryErrorCode
{
    ContainsNul,
    ContainsReservedBoundary,
    TooManyScalars,
    TooManyAtoms,
    TooManyTranscriptTokens,
    FtsExpressionTooLong,
}

public sealed record QueryError(QueryErrorCode Code, string Message);

public sealed record QueryParseResult(ParsedQuery? Query, QueryError? Error)
{
    public bool IsSuccess => Query is not null && Error is null;

    public static QueryParseResult Success(ParsedQuery query) => new(query, null);

    public static QueryParseResult Failure(QueryErrorCode code, string message) =>
        new(null, new QueryError(code, message));
}
