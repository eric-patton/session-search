using SessionSearch.Core.Models;
using SessionSearch.Core.Providers;
using SessionSearch.Core.Search;

namespace SessionSearch.Core.Sessions;

public sealed record SessionDocument(
    SessionIdentity Identity,
    string SourcePath,
    string Title,
    string Description,
    string Directory,
    string? Branch,
    string? Model,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset LastActivityUtc,
    long SourceBytes,
    bool Archived,
    bool FormatSupported,
    bool SourcePresent,
    int ParserVersion);

public sealed record SessionSegment(
    long Ordinal,
    ProviderRecordKind Role,
    DateTimeOffset? TimestampUtc,
    ProviderRecordKind Kind,
    bool IsChild,
    string Text);

public enum SearchScope
{
    All,
    ClaudeCode,
    Codex,
    Starred,
}

public enum SearchContentMode
{
    All,
    MetadataOnly,
}

public sealed record SessionSearchRequest(
    ParsedQuery Query,
    SearchScope Scope = SearchScope.All,
    int Page = 0,
    int PageSize = 50,
    string? DirectoryFilter = null,
    SearchContentMode ContentMode = SearchContentMode.All)
{
    public int SafePage => Math.Max(0, Page);

    public int SafePageSize => Math.Clamp(PageSize, 1, 50);
}

public sealed record SessionSearchResult(
    SessionDocument Session,
    MatchClass? MatchClass,
    double Bm25,
    string? Snippet,
    bool SnippetFromChild,
    bool IsSessionFavorite,
    bool IsDirectoryFavorite);

public sealed record SessionSearchPage(
    IReadOnlyList<SessionSearchResult> Results,
    int TotalCount,
    bool IsPartial);
