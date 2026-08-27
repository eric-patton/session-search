using SessionSearch.App;
using SessionSearch.Core.Models;
using SessionSearch.Core.Sessions;

namespace SessionSearch.AcceptanceTests;

public sealed class VirtualSessionResultsTests
{
    [Fact]
    // feat-001/AC-21
    public void Feat001Ac21EveryLogicalResultIsReachableThroughSparsePages()
    {
        var results = new VirtualSessionResults(pageSize: 2);
        results.BeginGeneration(1);

        Assert.True(results.ApplyPage(1, 0, Page(5, Result(1), Result(2))));
        Assert.True(results.ApplyPage(1, 1, Page(5, Result(3), Result(4))));
        Assert.True(results.ApplyPage(1, 2, Page(5, Result(5))));

        Assert.Equal(5, results.TotalCount);
        Assert.Equal(5, results.LoadedCount);
        Assert.Equal([0, 1, 2], results.LoadedPageNumbers);
        Assert.Equal(
            Enumerable.Range(1, 5).Select(Id).ToArray(),
            Enumerable.Range(0, 5)
                .Select(index =>
                {
                    Assert.True(results.TryGet(index, out SessionSearchResult? result));
                    return result!.Session.Identity.SessionId;
                })
                .ToArray());
    }

    [Fact]
    // feat-001/AC-21
    public void Feat001Ac21DuplicatePageRequestsCoalesceAndStaleResultsAreRejected()
    {
        var results = new VirtualSessionResults();
        results.BeginGeneration(4);

        Assert.True(results.TryBeginPageRequest(4, 2));
        Assert.False(results.TryBeginPageRequest(4, 2));
        results.BeginGeneration(5);

        Assert.False(results.ApplyPage(4, 2, Page(51, Result(1))));
        Assert.False(results.TryBeginPageRequest(4, 0));
        Assert.True(results.TryBeginPageRequest(5, 0));
    }

    [Fact]
    // feat-001/AC-21
    public void Feat001Ac21ColdPagesAreBoundedWhileRecentAndProtectedPagesRemain()
    {
        var results = new VirtualSessionResults(pageSize: 2, maxCachedPages: 2);
        results.BeginGeneration(1);
        results.ApplyPage(1, 0, Page(8, Result(1), Result(2)));
        results.ApplyPage(1, 1, Page(8, Result(3), Result(4)));
        Assert.True(results.TryGet(0, out _));

        results.ApplyPage(1, 2, Page(8, Result(5), Result(6)));
        Assert.Equal([0, 2], results.LoadedPageNumbers);
        Assert.False(results.TryGet(2, out _));

        results.ApplyPage(1, 3, Page(8, Result(7), Result(8)), new HashSet<int> { 0, 2 });
        Assert.Contains(0, results.LoadedPageNumbers);
        Assert.Contains(2, results.LoadedPageNumbers);
        Assert.Contains(3, results.LoadedPageNumbers);
    }

    [Fact]
    // feat-001/AC-21
    public void Feat001Ac21IdentityMappingTracksReorderedAndRemovedRows()
    {
        var results = new VirtualSessionResults(pageSize: 3);
        results.BeginGeneration(1);
        results.ApplyPage(1, 0, Page(3, Result(1), Result(2), Result(3)));
        SessionIdentity selected = Result(2).Session.Identity;

        results.ApplyPage(1, 0, Page(3, Result(3), Result(1), Result(2)));
        Assert.Equal(2, results.FindIndex(selected));

        results.ApplyPage(1, 0, Page(2, Result(3), Result(1)));
        Assert.Equal(-1, results.FindIndex(selected));
        Assert.False(results.TryGet(2, out _));
    }

    [Fact]
    // feat-001/AC-21
    public void Feat001Ac21ExecutableAndManagedResourceContainWindowsIconFrames()
    {
        using Stream? resource = typeof(VirtualSessionResults).Assembly.GetManifestResourceStream(
            "SessionSearch.App.Assets.SessionSearch.ico");
        Assert.NotNull(resource);
        using var reader = new BinaryReader(resource);
        Assert.Equal(0, reader.ReadUInt16());
        Assert.Equal(1, reader.ReadUInt16());
        ushort count = reader.ReadUInt16();
        var sizes = new HashSet<int>();
        for (int index = 0; index < count; index++)
        {
            int width = reader.ReadByte();
            int height = reader.ReadByte();
            reader.ReadByte();
            reader.ReadByte();
            reader.ReadUInt16();
            ushort bitCount = reader.ReadUInt16();
            uint byteCount = reader.ReadUInt32();
            uint offset = reader.ReadUInt32();
            width = width == 0 ? 256 : width;
            height = height == 0 ? 256 : height;
            Assert.Equal(width, height);
            Assert.Equal(32, bitCount);
            Assert.True(byteCount > 0);
            Assert.InRange(offset + byteCount, 1u, checked((uint)resource.Length));
            sizes.Add(width);
        }

        Assert.Contains(16, sizes);
        Assert.Contains(32, sizes);
        Assert.Contains(48, sizes);
        Assert.Contains(256, sizes);

        string appHost = Path.Combine(
            Path.GetDirectoryName(typeof(VirtualSessionResults).Assembly.Location)!,
            "SessionSearch.exe");
        Assert.True(File.Exists(appHost));
        using Icon? executableIcon = Icon.ExtractAssociatedIcon(appHost);
        Assert.NotNull(executableIcon);
    }

    private static SessionSearchPage Page(
        int totalCount,
        params SessionSearchResult[] results) => new(results, totalCount, false);

    private static SessionSearchResult Result(int value)
    {
        Guid id = Id(value);
        var session = new SessionDocument(
            new SessionIdentity(SessionProvider.ClaudeCode, id),
            $@"C:\Sessions\{value}.jsonl",
            $"Session {value}",
            $"Description {value}",
            $@"C:\Repos\Project{value}",
            null,
            null,
            null,
            DateTimeOffset.UnixEpoch.AddMinutes(value),
            100,
            false,
            true,
            true,
            1);
        return new SessionSearchResult(session, null, 0, null, false, false, false);
    }

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:000000000000}");
}
