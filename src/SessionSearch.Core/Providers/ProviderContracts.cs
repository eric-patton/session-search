using SessionSearch.Core.Models;
using SessionSearch.Core.Text;

namespace SessionSearch.Core.Providers;

public static class ProviderLimits
{
    public const int MaxCandidatesPerProvider = 100_000;
    public const int MaxJsonDepth = 64;
    public const int MaxJsonlRecordBytes = 32 * 1024 * 1024;
    public const int MaxExtractedTextBytes = 8 * 1024 * 1024;
    public const int MaxStoredSegmentBytes = 64 * 1024;
    public const string SearchRecordBoundaryToken = "\U0010FFFD";
    public const string SearchRecordBoundary = "\n" + SearchRecordBoundaryToken + "\n";
}

public enum ProviderSourceKind
{
    TopLevel,
    Child,
}

public enum ProviderRecordKind
{
    UserText,
    AssistantText,
    ToolText,
    ExplicitName,
    AiTitle,
    Metadata,
}

public enum ProviderDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record ProviderDiagnostic(
    SessionProvider Provider,
    ProviderDiagnosticSeverity Severity,
    string Code,
    string SourceAlias,
    string Message,
    int? ParserVersion = null,
    int RetryState = 0,
    long? ElapsedMilliseconds = null,
    string? ExceptionType = null);

public sealed record ProviderSource(
    SessionIdentity Owner,
    string CanonicalPath,
    string RelativePath,
    ProviderSourceKind Kind,
    Guid? ChildSessionId,
    bool Archived,
    long Length,
    DateTimeOffset LastWriteUtc,
    int ParserVersion,
    string? FileIdentity = null);

public sealed record ProviderSessionSeed(
    SessionIdentity Identity,
    string Directory,
    string? Branch,
    string? Model,
    DateTimeOffset? CreatedUtc,
    DateTimeOffset LastActivityUtc,
    bool Archived,
    bool FormatSupported,
    IReadOnlyList<ProviderSource> Sources);

public sealed record ProviderDiscoveryResult(
    IReadOnlyList<ProviderSessionSeed> Sessions,
    IReadOnlyList<ProviderDiagnostic> Diagnostics,
    bool IsPartial);

public sealed record ProviderRecord(
    SessionIdentity Owner,
    string SourceRelativePath,
    long Sequence,
    DateTimeOffset? TimestampUtc,
    ProviderRecordKind Kind,
    string Text,
    UserTextKind? UserTextKind,
    bool IsChild);

public sealed record ProviderReadResult(
    IReadOnlyList<ProviderRecord> Records,
    IReadOnlyList<ProviderDiagnostic> Diagnostics,
    long LastCompleteOffset,
    bool IsPartial);

public interface ISessionProviderAdapter
{
    SessionProvider Provider { get; }

    ValueTask<ProviderDiscoveryResult> DiscoverAsync(
        string rootPath,
        CancellationToken cancellationToken);

    ValueTask<ProviderReadResult> ReadAsync(
        ProviderSource source,
        long startOffset,
        CancellationToken cancellationToken);
}
