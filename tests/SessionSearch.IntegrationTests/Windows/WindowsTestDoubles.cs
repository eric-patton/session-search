using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.IntegrationTests.Windows;

internal sealed class FakeWindowsPathProbe : IWindowsPathProbe
{
    public int Calls { get; private set; }

    public DriveType DriveType { get; set; } = DriveType.Fixed;

    public bool DirectoryPresent { get; set; } = true;

    public bool FilePresent { get; set; } = true;

    public bool ReparsePoint { get; set; }

    public string? FinalPath { get; set; }

    public DriveType GetDriveType(string driveRoot)
    {
        Calls++;
        return DriveType;
    }

    public bool DirectoryExists(string path)
    {
        Calls++;
        return DirectoryPresent;
    }

    public bool FileExists(string path)
    {
        Calls++;
        return FilePresent;
    }

    public bool HasReparsePoint(string path)
    {
        Calls++;
        return ReparsePoint;
    }

    public string GetFinalPath(string path, bool directory)
    {
        Calls++;
        return FinalPath ?? path;
    }
}

internal sealed class FakeExecutableTrustVerifier : IExecutableTrustVerifier
{
    public int Calls { get; private set; }

    public ExecutableTrustVerification Verification { get; set; } = new(
        true,
        "Test Publisher",
        "identity-1");

    public ExecutableTrustVerification Verify(
        string canonicalPath,
        TrustedExecutableProfile profile)
    {
        Calls++;
        return Verification;
    }
}

internal sealed class FakeResumePlanRevalidator : IResumePlanRevalidator
{
    private readonly Queue<bool> outcomes;

    public FakeResumePlanRevalidator(params bool[] outcomes)
    {
        this.outcomes = new Queue<bool>(outcomes.Length == 0 ? [true] : outcomes);
    }

    public int Calls { get; private set; }

    public bool Revalidate(ResumePlan plan, out string reason)
    {
        Calls++;
        bool outcome = outcomes.Count > 1 ? outcomes.Dequeue() : outcomes.Peek();
        reason = outcome ? string.Empty : "Injected revalidation failure.";
        return outcome;
    }
}
