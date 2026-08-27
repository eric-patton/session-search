using System.Globalization;
using SessionSearch.Core.Models;
using SessionSearch.Core.Search;

namespace SessionSearch.Core.Tests;

public sealed class SearchResultComparerTests
{
    // feat-001/AC-4
    [Fact]
    public void Feat001Ac4UsesTheCompleteStableOrderingTuple()
    {
        DateTimeOffset time = DateTimeOffset.Parse(
            "2026-08-26T10:00:00Z",
            CultureInfo.InvariantCulture);
        SearchRank earliestClass = Rank(SessionProvider.Codex, "00000000-0000-0000-0000-000000000003", MatchClass.ExactTitle, time, 50);
        SearchRank betterTranscript = Rank(SessionProvider.Codex, "00000000-0000-0000-0000-000000000002", MatchClass.Transcript, time, 1);
        SearchRank worseTranscript = Rank(SessionProvider.ClaudeCode, "00000000-0000-0000-0000-000000000001", MatchClass.Transcript, time, 9);

        SearchRank[] values = [worseTranscript, betterTranscript, earliestClass];
        Array.Sort(values, SearchRankComparer.Instance);

        Assert.Equal([earliestClass, betterTranscript, worseTranscript], values);
    }

    // feat-001/AC-4
    [Fact]
    public void Feat001Ac4UsesRecencyThenProviderThenOrdinalIdAsFinalTieBreakers()
    {
        DateTimeOffset old = DateTimeOffset.Parse(
            "2026-08-25T10:00:00Z",
            CultureInfo.InvariantCulture);
        DateTimeOffset recent = old.AddDays(1);
        SearchRank[] values =
        [
            Rank(SessionProvider.Codex, "00000000-0000-0000-0000-000000000002", MatchClass.TitleMetadata, recent),
            Rank(SessionProvider.ClaudeCode, "00000000-0000-0000-0000-000000000002", MatchClass.TitleMetadata, recent),
            Rank(SessionProvider.ClaudeCode, "00000000-0000-0000-0000-000000000001", MatchClass.TitleMetadata, recent),
            Rank(SessionProvider.ClaudeCode, "00000000-0000-0000-0000-000000000003", MatchClass.TitleMetadata, old),
        ];

        Array.Sort(values, SearchRankComparer.Instance);

        Assert.Equal("00000000-0000-0000-0000-000000000001", values[0].Identity.SessionId.ToString("D"));
        Assert.Equal(SessionProvider.ClaudeCode, values[1].Identity.Provider);
        Assert.Equal(SessionProvider.Codex, values[2].Identity.Provider);
        Assert.Equal(old, values[3].LastActivityUtc);
    }

    private static SearchRank Rank(
        SessionProvider provider,
        string id,
        MatchClass matchClass,
        DateTimeOffset lastActivity,
        double bm25 = 0) => new(
            new SessionIdentity(provider, Guid.Parse(id)),
            matchClass,
            bm25,
            lastActivity);
}
