using SessionSearch.Core.Models;

namespace SessionSearch.Core.Search;

public enum MatchClass
{
    ExactTitle = 0,
    ExactDirectory = 1,
    TitlePrefix = 2,
    TitleMetadata = 3,
    DescriptionMetadata = 4,
    DirectoryMetadata = 5,
    OtherMetadata = 6,
    Transcript = 7,
}

public sealed record SearchRank(
    SessionIdentity Identity,
    MatchClass MatchClass,
    double Bm25,
    DateTimeOffset LastActivityUtc);

public sealed class SearchRankComparer : IComparer<SearchRank>
{
    public static SearchRankComparer Instance { get; } = new();

    private SearchRankComparer()
    {
    }

    public int Compare(SearchRank? left, SearchRank? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        int comparison = left.MatchClass.CompareTo(right.MatchClass);
        if (comparison != 0)
        {
            return comparison;
        }

        if (left.MatchClass == MatchClass.Transcript)
        {
            comparison = left.Bm25.CompareTo(right.Bm25);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        comparison = right.LastActivityUtc.CompareTo(left.LastActivityUtc);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = left.Identity.Provider.CompareTo(right.Identity.Provider);
        if (comparison != 0)
        {
            return comparison;
        }

        return StringComparer.Ordinal.Compare(
            left.Identity.SessionId.ToString("D"),
            right.Identity.SessionId.ToString("D"));
    }
}
