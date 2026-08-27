using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.IntegrationTests.Windows;

public sealed class LocalPathPolicyTests
{
    [Theory]
    [InlineData(@"\\attacker.example\share\session")]
    [InlineData(@"\\?\C:\provider")]
    [InlineData(@"\??\C:\provider")]
    [InlineData(@"C:\provider\record.jsonl:secret")]
    [InlineData(@"relative\provider")]
    [InlineData(@"C:\NUL\record.jsonl")]
    // feat-001/AC-18
    public void Feat001Ac18LexicalRejectionOccursBeforeAnyFilesystemProbe(string path)
    {
        var probe = new FakeWindowsPathProbe();
        var policy = new LocalPathPolicy(probe);

        LocalPathValidation result = policy.ValidateExistingDirectory(path);

        Assert.False(result.IsSafe);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18LocalFixedDirectoryReturnsItsFinalCanonicalPath()
    {
        var probe = new FakeWindowsPathProbe
        {
            FinalPath = @"C:\Provider\Sessions",
        };
        var policy = new LocalPathPolicy(probe);

        LocalPathValidation result = policy.ValidateExistingDirectory(@"C:\Provider\Sessions");

        Assert.True(result.IsSafe);
        Assert.Equal(@"C:\Provider\Sessions", result.CanonicalPath);
        Assert.True(probe.Calls > 0);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18NonFixedDriveIsRejected()
    {
        var probe = new FakeWindowsPathProbe
        {
            DriveType = DriveType.Network,
        };
        var policy = new LocalPathPolicy(probe);

        LocalPathValidation result = policy.ValidateExistingDirectory(@"Z:\Provider");

        Assert.Equal(LocalPathFailure.NonFixedDrive, result.Failure);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18ReparsePointIsRejected()
    {
        var probe = new FakeWindowsPathProbe
        {
            ReparsePoint = true,
        };
        var policy = new LocalPathPolicy(probe);

        LocalPathValidation result = policy.ValidateExistingDirectory(@"C:\Provider\Junction");

        Assert.Equal(LocalPathFailure.ReparsePoint, result.Failure);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18FinalHandlePathMustRemainInsideTrustedRoot()
    {
        var probe = new FakeWindowsPathProbe
        {
            FinalPath = @"C:\Outside\record.jsonl",
        };
        var policy = new LocalPathPolicy(probe);

        LocalPathValidation result = policy.ValidateExistingFile(
            @"C:\Provider\record.jsonl",
            "record.jsonl",
            trustedRoot: @"C:\Provider");

        Assert.Equal(LocalPathFailure.EscapesTrustedRoot, result.Failure);
    }
}
