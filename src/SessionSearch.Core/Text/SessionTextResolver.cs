namespace SessionSearch.Core.Text;

public sealed record TimestampedText(string Text, long Sequence);

public enum UserTextKind
{
    Human,
    System,
    Developer,
    Tool,
    Summary,
    Telemetry,
    Synthetic,
    SyntheticMetadata,
}

public sealed record UserTextEvidence(
    string Text,
    long Sequence,
    UserTextKind Kind);

public sealed record SessionTextEvidence(
    IReadOnlyList<TimestampedText> ExplicitNames,
    IReadOnlyList<TimestampedText> AiTitles,
    IReadOnlyList<UserTextEvidence> UserTexts,
    IReadOnlyList<TimestampedText>? AppOverrides = null);

public sealed record ResolvedSessionText(string Title, string Description);

public static class SessionTextResolver
{
    public static ResolvedSessionText Resolve(
        string immutableSessionId,
        SessionTextEvidence evidence)
    {
        IReadOnlyList<TimestampedText> appOverrides = evidence.AppOverrides ?? [];
        IReadOnlyList<UserTextEvidence> humanTexts = evidence.UserTexts
            .Where(item => item.Kind == UserTextKind.Human)
            .OrderBy(item => item.Sequence)
            .ToArray();

        string? title = LatestUsable(appOverrides)
            ?? LatestUsable(evidence.ExplicitNames)
            ?? LatestUsable(evidence.AiTitles)
            ?? humanTexts.Select(item => Sanitize(item.Text)).FirstOrDefault(IsUsable)
            ?? Sanitize(immutableSessionId);

        string description = humanTexts
            .Select(item => Sanitize(item.Text))
            .Where(IsUsable)
            .LastOrDefault() ?? string.Empty;

        return new ResolvedSessionText(
            title,
            TextNormalization.TruncateDescription(description));
    }

    private static string? LatestUsable(IEnumerable<TimestampedText> values) =>
        values
            .OrderBy(item => item.Sequence)
            .Select(item => Sanitize(item.Text))
            .Where(IsUsable)
            .LastOrDefault();

    private static string Sanitize(string value) => DisplayTextSanitizer.Sanitize(value);

    private static bool IsUsable(string? value) => !string.IsNullOrWhiteSpace(value);
}
