using SessionSearch.Core.Models;
using SessionSearch.Infrastructure.Storage;

namespace SessionSearch.IntegrationTests.Storage;

public sealed class FavoritesRepositoryTests
{
    // feat-001/AC-7
    [Fact]
    public async Task Feat001Ac7PersistsSessionAndDirectoryFavoritesIndependently()
    {
        using TestWorkspace workspace = new();
        SessionIdentity identity = new(
            SessionProvider.ClaudeCode,
            Guid.Parse("11111111-1111-1111-1111-111111111111"));

        await using (SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken))
        {
            FavoritesRepository favorites = new(database);
            await favorites.SetSessionFavoriteAsync(
                identity,
                isFavorite: true,
                TestContext.Current.CancellationToken);
            await favorites.SetDirectoryFavoriteAsync(
                @"C:\repos\fixture\",
                isFavorite: true,
                TestContext.Current.CancellationToken);
        }

        await using (SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken))
        {
            FavoritesRepository favorites = new(database);
            Assert.True(await favorites.IsSessionFavoriteAsync(
                identity,
                TestContext.Current.CancellationToken));
            Assert.True(await favorites.IsDirectoryFavoriteAsync(
                @"c:\REPOS\fixture",
                TestContext.Current.CancellationToken));

            await favorites.SetSessionFavoriteAsync(
                identity,
                isFavorite: false,
                TestContext.Current.CancellationToken);
            Assert.False(await favorites.IsSessionFavoriteAsync(
                identity,
                TestContext.Current.CancellationToken));
            Assert.True(await favorites.IsDirectoryFavoriteAsync(
                @"C:\repos\fixture",
                TestContext.Current.CancellationToken));
        }
    }

    // feat-001/AC-7
    [Fact]
    public async Task Feat001Ac7DirectoryKeysPreserveMeaningfulWhitespace()
    {
        using TestWorkspace workspace = new();
        string oneSpace = Path.Combine(workspace.Root, "alpha beta");
        string twoSpaces = Path.Combine(workspace.Root, "alpha  beta");

        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            workspace.DatabasePath,
            protectDirectory: false,
            TestContext.Current.CancellationToken);
        FavoritesRepository favorites = new(database);
        await favorites.SetDirectoryFavoriteAsync(
            oneSpace,
            isFavorite: true,
            TestContext.Current.CancellationToken);

        Assert.True(await favorites.IsDirectoryFavoriteAsync(
            oneSpace,
            TestContext.Current.CancellationToken));
        Assert.False(await favorites.IsDirectoryFavoriteAsync(
            twoSpaces,
            TestContext.Current.CancellationToken));
    }
}
