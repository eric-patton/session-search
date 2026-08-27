using SessionSearch.Core.Providers;
using SessionSearch.Core.Search;

namespace SessionSearch.Core.Tests;

public sealed class QueryParserTests
{
    // feat-001/AC-4
    [Theory]
    [InlineData("tile error", 2, false, "TILE ERROR")]
    [InlineData("  tile   error  ", 2, false, "TILE ERROR")]
    [InlineData("\"tile loading\" error", 2, false, "TILE LOADING ERROR")]
    [InlineData("\"unterminated phrase", 2, false, "UNTERMINATED PHRASE")]
    [InlineData("   ", 0, true, "")]
    public void Feat001Ac4ParsesTheDocumentedGrammar(
        string text,
        int atomCount,
        bool isBrowse,
        string normalizedText)
    {
        QueryParseResult result = QueryParser.Parse(text);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Query);
        Assert.Equal(atomCount, result.Query.Atoms.Count);
        Assert.Equal(isBrowse, result.Query.IsBrowse);
        Assert.Equal(normalizedText, result.Query.NormalizedText);
    }

    // feat-001/AC-4
    [Fact]
    public void Feat001Ac4QuotesFtsOperatorsAsLiteralTokens()
    {
        QueryParseResult result = QueryParser.Parse("OR NEAR(foo) * title:fake");

        Assert.True(result.IsSuccess);
        Assert.Equal(
            "\"OR\"* AND \"NEAR\" AND \"foo\"* AND \"title\" AND \"fake\"*",
            result.Query!.FtsExpression);
    }

    // feat-001/AC-4
    [Fact]
    public void Feat001Ac4RejectsNulBeforeProducingAQuery()
    {
        QueryParseResult result = QueryParser.Parse("tile\0error");

        Assert.False(result.IsSuccess);
        Assert.Equal(QueryErrorCode.ContainsNul, result.Error!.Code);
        Assert.Null(result.Query);
    }

    // feat-001/AC-4
    [Theory]
    [InlineData("{0}")]
    [InlineData("\"{0}\"")]
    [InlineData("tile{0}error")]
    public void Feat001Ac4RejectsTheReservedIndexBoundaryCharacter(string format)
    {
        QueryParseResult result = QueryParser.Parse(
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                format,
                ProviderLimits.SearchRecordBoundaryToken));

        Assert.False(result.IsSuccess);
        Assert.Equal(QueryErrorCode.ContainsReservedBoundary, result.Error!.Code);
        Assert.Null(result.Query);
    }

    // feat-001/AC-4
    [Fact]
    public void Feat001Ac4RejectsMoreThan512UnicodeScalars()
    {
        string text = new('x', QueryLimits.MaxScalars + 1);

        QueryParseResult result = QueryParser.Parse(text);

        Assert.False(result.IsSuccess);
        Assert.Equal(QueryErrorCode.TooManyScalars, result.Error!.Code);
    }

    // feat-001/AC-4
    [Fact]
    public void Feat001Ac4RejectsMoreThan32Atoms()
    {
        string text = string.Join(' ', Enumerable.Repeat("word", QueryLimits.MaxAtoms + 1));

        QueryParseResult result = QueryParser.Parse(text);

        Assert.False(result.IsSuccess);
        Assert.Equal(QueryErrorCode.TooManyAtoms, result.Error!.Code);
    }

    // feat-001/AC-4
    [Fact]
    public void Feat001Ac4PreservesAWindowsPathForMetadataMatching()
    {
        QueryParseResult result = QueryParser.Parse(@"C:\repos\todo");

        Assert.True(result.IsSuccess);
        Assert.Equal(@"C:\REPOS\TODO", result.Query!.Atoms.Single().NormalizedText);
        Assert.Equal("\"C\" AND \"repos\" AND \"todo\"*", result.Query.FtsExpression);
    }

    // feat-001/AC-4
    [Fact]
    public void Feat001Ac4UsesUnicode61BoundariesForUnderscores()
    {
        QueryParseResult result = QueryParser.Parse("foo_bar");

        Assert.True(result.IsSuccess);
        Assert.Equal(["foo", "bar"], result.Query!.Atoms.Single().TranscriptTokens);
        Assert.Equal("\"foo\" AND \"bar\"*", result.Query.FtsExpression);
    }

    // feat-001/AC-4
    [Fact]
    public void Feat001Ac4RejectsMoreThan128Unicode61TokensSeparatedByUnderscores()
    {
        string acceptedText = string.Join(
            '_',
            Enumerable.Repeat("a", QueryLimits.MaxTranscriptTokens));
        string rejectedText = acceptedText + "_a";

        QueryParseResult accepted = QueryParser.Parse(acceptedText);
        QueryParseResult rejected = QueryParser.Parse(rejectedText);

        Assert.True(accepted.IsSuccess);
        Assert.Equal(
            QueryLimits.MaxTranscriptTokens,
            accepted.Query!.Atoms.Single().TranscriptTokens.Count);
        Assert.False(rejected.IsSuccess);
        Assert.Equal(QueryErrorCode.TooManyTranscriptTokens, rejected.Error!.Code);
    }
}
