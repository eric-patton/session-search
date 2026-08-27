using SessionSearch.Core.Models;
using SessionSearch.Core.Sessions;

namespace SessionSearch.App;

public sealed class VirtualSessionResults(int pageSize = 50, int maxCachedPages = 16)
{
    private readonly Dictionary<int, IReadOnlyList<SessionSearchResult>> pages = [];
    private readonly HashSet<int> pendingPages = [];
    private readonly LinkedList<int> pageRecency = [];
    private readonly Dictionary<int, LinkedListNode<int>> recencyNodes = [];

    public int PageSize { get; } = Math.Clamp(pageSize, 1, 50);

    public int MaxCachedPages { get; } = Math.Max(1, maxCachedPages);

    public int Generation { get; private set; }

    public int TotalCount { get; private set; }

    public int LoadedCount => pages.Values.Sum(page => page.Count);

    public IReadOnlyList<int> LoadedPageNumbers => pages.Keys.Order().ToArray();

    public void BeginGeneration(int generation)
    {
        if (generation <= Generation)
        {
            throw new ArgumentOutOfRangeException(
                nameof(generation),
                "A result generation must increase monotonically.");
        }

        Generation = generation;
        TotalCount = 0;
        pages.Clear();
        pendingPages.Clear();
        pageRecency.Clear();
        recencyNodes.Clear();
    }

    public bool TryBeginPageRequest(int generation, int pageNumber)
    {
        if (generation != Generation || pageNumber < 0 || pages.ContainsKey(pageNumber))
        {
            return false;
        }

        return pendingPages.Add(pageNumber);
    }

    public void EndPageRequest(int generation, int pageNumber)
    {
        if (generation == Generation)
        {
            pendingPages.Remove(pageNumber);
        }
    }

    public bool ApplyPage(
        int generation,
        int pageNumber,
        SessionSearchPage page,
        IReadOnlySet<int>? protectedPages = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        if (generation != Generation || pageNumber < 0)
        {
            return false;
        }

        int previousTotal = TotalCount;
        TotalCount = Math.Max(0, page.TotalCount);
        int startIndex = checked(pageNumber * PageSize);
        int allowed = Math.Clamp(TotalCount - startIndex, 0, PageSize);
        SessionSearchResult[] bounded = page.Results.Take(allowed).ToArray();
        bool changed = previousTotal != TotalCount ||
            !pages.TryGetValue(pageNumber, out IReadOnlyList<SessionSearchResult>? existing) ||
            !existing.SequenceEqual(bounded);
        pages[pageNumber] = bounded;
        Touch(pageNumber);
        pendingPages.Remove(pageNumber);

        int lastPage = TotalCount == 0 ? -1 : (TotalCount - 1) / PageSize;
        foreach (int stalePage in pages.Keys.Where(key => key > lastPage).ToArray())
        {
            pages.Remove(stalePage);
            RemoveRecency(stalePage);
            changed = true;
        }

        EvictColdPages(pageNumber, protectedPages);

        return changed;
    }

    public bool TryGet(int index, out SessionSearchResult? result)
    {
        result = null;
        if ((uint)index >= (uint)TotalCount)
        {
            return false;
        }

        int pageNumber = index / PageSize;
        int pageOffset = index % PageSize;
        if (!pages.TryGetValue(pageNumber, out IReadOnlyList<SessionSearchResult>? page) ||
            (uint)pageOffset >= (uint)page.Count)
        {
            return false;
        }

        result = page[pageOffset];
        Touch(pageNumber);
        return true;
    }

    public int FindIndex(SessionIdentity identity)
    {
        foreach ((int pageNumber, IReadOnlyList<SessionSearchResult> page) in pages)
        {
            for (int pageOffset = 0; pageOffset < page.Count; pageOffset++)
            {
                if (page[pageOffset].Session.Identity == identity)
                {
                    return checked((pageNumber * PageSize) + pageOffset);
                }
            }
        }

        return -1;
    }

    public SessionSearchResult[] GetLoadedResults() => pages
        .OrderBy(pair => pair.Key)
        .SelectMany(pair => pair.Value)
        .ToArray();

    private void Touch(int pageNumber)
    {
        RemoveRecency(pageNumber);
        recencyNodes[pageNumber] = pageRecency.AddLast(pageNumber);
    }

    private void RemoveRecency(int pageNumber)
    {
        if (recencyNodes.Remove(pageNumber, out LinkedListNode<int>? node))
        {
            pageRecency.Remove(node);
        }
    }

    private void EvictColdPages(int currentPage, IReadOnlySet<int>? protectedPages)
    {
        while (pages.Count > MaxCachedPages)
        {
            LinkedListNode<int>? candidate = pageRecency.First;
            while (candidate is not null &&
                (candidate.Value == currentPage ||
                    (protectedPages?.Contains(candidate.Value) ?? false) ||
                    pendingPages.Contains(candidate.Value)))
            {
                candidate = candidate.Next;
            }

            if (candidate is null)
            {
                return;
            }

            int pageNumber = candidate.Value;
            pages.Remove(pageNumber);
            RemoveRecency(pageNumber);
        }
    }
}
