namespace SessionSearch.Core.Models;

public enum SessionProvider
{
    ClaudeCode = 0,
    Codex = 1,
}

public readonly record struct SessionIdentity(
    SessionProvider Provider,
    Guid SessionId);

public enum AvailabilityStatus
{
    UnsupportedFormat = 0,
    SourceRemoved = 1,
    Archived = 2,
    Active = 3,
    PossiblyActive = 4,
    UnsafeDirectory = 5,
    MissingDirectory = 6,
    MissingCli = 7,
    Ready = 8,
}
