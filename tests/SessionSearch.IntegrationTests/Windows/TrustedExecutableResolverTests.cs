using SessionSearch.Infrastructure.Windows;
using SessionSearch.IntegrationTests.Storage;

namespace SessionSearch.IntegrationTests.Windows;

public sealed class TrustedExecutableResolverTests
{
    private static readonly TrustedExecutableProfile ClaudeProfile = new(
        TrustedExecutableKind.ClaudeCode,
        "claude.exe",
        ["Anthropic, PBC"]);

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18AbsoluteSignedExpectedPublisherExecutableResolves()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var verifier = new FakeExecutableTrustVerifier
        {
            Verification = new ExecutableTrustVerification(
                true,
                "Anthropic, PBC",
                "file-id-1"),
        };
        var resolver = new TrustedExecutableResolver(new LocalPathPolicy(pathProbe), verifier);

        ExecutableResolution result = resolver.Resolve(
            ClaudeProfile,
            [@"C:\Tools\claude.exe"]);

        Assert.True(result.IsResolved);
        Assert.Equal(@"C:\Tools\claude.exe", result.Executable!.CanonicalPath);
        Assert.Equal("file-id-1", result.Executable.FileIdentity);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18WrongPublisherIsRejected()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var verifier = new FakeExecutableTrustVerifier
        {
            Verification = new ExecutableTrustVerification(
                true,
                "Unexpected Publisher",
                "file-id-1"),
        };
        var resolver = new TrustedExecutableResolver(new LocalPathPolicy(pathProbe), verifier);

        ExecutableResolution result = resolver.Resolve(
            ClaudeProfile,
            [@"C:\Tools\claude.exe"]);

        Assert.False(result.IsResolved);
    }

    [Theory]
    [InlineData(@"\\attacker\share\claude.exe")]
    [InlineData(@"C:\Tools\claude.cmd")]
    [InlineData(@"claude.exe")]
    [InlineData(@"C:\Tools\claude.exe:payload")]
    // feat-001/AC-18
    public void Feat001Ac18HostileOrShimCandidatesAreRejectedBeforeTrustVerification(string candidate)
    {
        var pathProbe = new FakeWindowsPathProbe();
        var verifier = new FakeExecutableTrustVerifier();
        var resolver = new TrustedExecutableResolver(new LocalPathPolicy(pathProbe), verifier);

        ExecutableResolution result = resolver.Resolve(ClaudeProfile, [candidate]);

        Assert.False(result.IsResolved);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18WindowsTerminalAliasRequiresVerifiedPackageBacking()
    {
        var pathProbe = new FakeWindowsPathProbe
        {
            ReparsePoint = true,
        };
        var verifier = new FakeExecutableTrustVerifier
        {
            Verification = new ExecutableTrustVerification(
                true,
                "Microsoft Corporation",
                "terminal-id",
                IsVerifiedPackageAlias: false),
        };
        var resolver = new TrustedExecutableResolver(new LocalPathPolicy(pathProbe), verifier);
        var profile = new TrustedExecutableProfile(
            TrustedExecutableKind.WindowsTerminal,
            "wt.exe",
            ["Microsoft Corporation"]);

        ExecutableResolution result = resolver.Resolve(
            profile,
            [@"C:\Users\Eric\AppData\Local\Microsoft\WindowsApps\wt.exe"]);

        Assert.False(result.IsResolved);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18WindowsTerminalPackageBinaryRequiresVerifiedPackageBacking()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var verifier = new FakeExecutableTrustVerifier
        {
            Verification = new ExecutableTrustVerification(
                true,
                "Microsoft Corporation",
                "terminal-id",
                IsVerifiedPackageAlias: false),
        };
        var resolver = new TrustedExecutableResolver(new LocalPathPolicy(pathProbe), verifier);
        var profile = new TrustedExecutableProfile(
            TrustedExecutableKind.WindowsTerminal,
            "wt.exe",
            ["Microsoft Corporation"]);

        ExecutableResolution result = resolver.Resolve(
            profile,
            [@"C:\Program Files\WindowsApps\Microsoft.WindowsTerminal_1.24.1.0_x64__8wekyb3d8bbwe\wt.exe"]);

        Assert.False(result.IsResolved);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18RevalidationRejectsChangedFileIdentity()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var verifier = new FakeExecutableTrustVerifier
        {
            Verification = new ExecutableTrustVerification(
                true,
                "Anthropic, PBC",
                "file-id-1"),
        };
        var resolver = new TrustedExecutableResolver(new LocalPathPolicy(pathProbe), verifier);
        ResolvedExecutable initial = resolver.Resolve(
            ClaudeProfile,
            [@"C:\Tools\claude.exe"]).Executable!;
        verifier.Verification = verifier.Verification with { FileIdentity = "file-id-2" };

        bool result = resolver.Revalidate(initial);

        Assert.False(result);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18CodexPatternsIncludeCurrentProgramsInstallLayout()
    {
        IReadOnlyList<string> candidates = InstalledExecutablePatterns.GetCandidates(
            TrustedExecutableKind.Codex,
            @"C:\Users\Eric",
            @"C:\Users\Eric\AppData\Local");

        Assert.Contains(
            @"C:\Users\Eric\AppData\Local\Programs\OpenAI\Codex\bin\codex.exe",
            candidates);
        Assert.DoesNotContain(candidates, path => path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    // feat-001/AC-9 feat-001/AC-18
    public void Feat001Ac9WindowsTerminalPatternsPreferTheSignedPackageBinaryOverTheAlias()
    {
        using var workspace = new TestWorkspace();
        string packageDirectory = Directory.CreateDirectory(Path.Combine(
            workspace.Root,
            "WindowsApps",
            "Microsoft.WindowsTerminal_1.24.1.0_x64__8wekyb3d8bbwe")).FullName;
        string packageBinary = Path.Combine(packageDirectory, "wt.exe");
        File.WriteAllBytes(packageBinary, [0x4D, 0x5A]);

        IReadOnlyList<string> candidates = InstalledExecutablePatterns.GetCandidates(
            TrustedExecutableKind.WindowsTerminal,
            @"C:\Users\Eric",
            @"C:\Users\Eric\AppData\Local",
            workspace.Root);

        Assert.Equal(packageBinary, candidates[0]);
        Assert.Equal(
            @"C:\Users\Eric\AppData\Local\Microsoft\WindowsApps\wt.exe",
            candidates[^1]);
    }
}
