using System.Text;
using SessionSearch.Core.Text;

namespace SessionSearch.Core.Tests;

public sealed class SessionTextResolverTests
{
    // feat-001/AC-6
    [Fact]
    public void Feat001Ac6ExplicitNameOutranksLaterAiTitle()
    {
        SessionTextEvidence evidence = new(
            ExplicitNames: [new("Pinned title", 2)],
            AiTitles: [new("Early AI title", 1), new("Later AI title", 3)],
            UserTexts: [new("First prompt", 1, UserTextKind.Human)]);

        ResolvedSessionText resolved = SessionTextResolver.Resolve(
            "11111111-1111-1111-1111-111111111111",
            evidence);

        Assert.Equal("Pinned title", resolved.Title);
    }

    // feat-001/AC-6
    [Fact]
    public void Feat001Ac6UsesLatestIncludedHumanTextAsDescription()
    {
        SessionTextEvidence evidence = new(
            ExplicitNames: [],
            AiTitles: [],
            UserTexts:
            [
                new("First request", 1, UserTextKind.Human),
                new("<system-reminder>control</system-reminder>", 2, UserTextKind.Synthetic),
                new("Keep <user-tag>literal markup</user-tag>", 3, UserTextKind.Human),
            ]);

        ResolvedSessionText resolved = SessionTextResolver.Resolve("fixture-id", evidence);

        Assert.Equal("First request", resolved.Title);
        Assert.Equal("Keep <user-tag>literal markup</user-tag>", resolved.Description);
    }

    // feat-001/AC-6
    [Fact]
    public void Feat001Ac6TruncatesToTheLastWhitespaceInside177Scalars()
    {
        string prefix = string.Join(' ', Enumerable.Repeat("word", 45));
        string value = prefix + " trailing text that must not fit";

        string result = TextNormalization.TruncateDescription(value);

        Assert.True(result.EnumerateRunes().Count() <= 180);
        Assert.EndsWith("...", result, StringComparison.Ordinal);
        Assert.DoesNotContain("trailing", result, StringComparison.Ordinal);
    }

    // feat-001/AC-6
    [Fact]
    public void Feat001Ac6HardCutsOneLongWordAt177Scalars()
    {
        string value = new('x', 181);

        string result = TextNormalization.TruncateDescription(value);

        Assert.Equal(180, result.EnumerateRunes().Count());
        Assert.Equal(new string('x', 177) + "...", result);
    }

    // feat-001/AC-6
    [Fact]
    public void Feat001Ac6NormalizesCompatibilityAndWhitespaceWithoutChangingDisplayCase()
    {
        string result = TextNormalization.NormalizeDisplay("  Tile\t\u212Aey\r\nValue  ");

        Assert.Equal("Tile Key Value", result);
    }

    // feat-001/AC-6
    [Fact]
    public void Feat001Ac6ReplacesBidirectionalAndControlCharactersForDisplay()
    {
        string result = DisplayTextSanitizer.Sanitize("Ready\u202Eevil\u0001\nnext");

        Assert.Equal("Ready\uFFFDevil\uFFFD next", result);
    }
}
