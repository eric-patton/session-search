using SessionSearch.Infrastructure.Windows;
using SessionSearch.IntegrationTests.Storage;

namespace SessionSearch.IntegrationTests.Windows;

public sealed class TrustedExecutableResolverTests
{
    private const string UserProfile = @"C:\Users\Eric";
    private const string LocalAppData = @"C:\Users\Eric\AppData\Local";
    private const string CodexAlias = @"C:\Users\Eric\AppData\Local\Programs\OpenAI\Codex\bin\codex.exe";
    private const string CodexAliasDirectory = @"C:\Users\Eric\AppData\Local\Programs\OpenAI\Codex\bin";
    private const string CodexCurrentDirectory = @"C:\Users\Eric\.codex\packages\standalone\current";
    private const string CodexCurrentBin = @"C:\Users\Eric\.codex\packages\standalone\current\bin";
    private const string CodexRelease = @"C:\Users\Eric\.codex\packages\standalone\releases\0.149.1-x86_64-pc-windows-msvc";
    private const string CodexFinalExecutable = @"C:\Users\Eric\.codex\packages\standalone\releases\0.149.1-x86_64-pc-windows-msvc\bin\codex.exe";
    private const string CodexSignerSubject = "CN=\"OpenAI OpCo, LLC\", O=\"OpenAI OpCo, LLC\", L=San Francisco, S=California, C=US";

    private static readonly TrustedExecutableProfile ClaudeProfile = new(
        TrustedExecutableKind.ClaudeCode,
        "claude.exe",
        ["Anthropic, PBC"]);

    private static readonly TrustedExecutableProfile CodexProfile = new(
        TrustedExecutableKind.Codex,
        "codex.exe",
        ["OpenAI OpCo, LLC"],
        new TrustedSignerPolicy("codex-signer-v1", [CodexSignerSubject]));

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
    public void Feat001Ac18OfficialCodexAliasResolvesOnlyToTheVerifiedFinalReleasePath()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var redirectReader = CreateExpectedCodexRedirects();
        var verifier = CreateCodexVerifier();
        TrustedExecutableResolver resolver = CreateCodexResolver(
            pathProbe,
            redirectReader,
            verifier);

        ExecutableResolution result = resolver.Resolve(CodexProfile, [CodexAlias]);

        Assert.True(result.IsResolved);
        Assert.Equal(CodexFinalExecutable, result.Executable!.CanonicalPath);
        Assert.Equal(CodexAlias, result.Executable.SourceAliasPath);
        Assert.Equal(CodexSignerSubject, result.Executable.SignerSubject);
        Assert.Equal([CodexFinalExecutable], verifier.Paths);
        Assert.Equal(2, redirectReader.Calls);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18ArbitraryCodexReparseAliasRemainsRejected()
    {
        const string arbitraryAlias = @"C:\Tools\codex.exe";
        var pathProbe = new FakeWindowsPathProbe();
        pathProbe.ReparsePaths.Add(arbitraryAlias);
        var redirectReader = CreateExpectedCodexRedirects();
        var verifier = CreateCodexVerifier();
        TrustedExecutableResolver resolver = CreateCodexResolver(
            pathProbe,
            redirectReader,
            verifier);

        ExecutableResolution result = resolver.Resolve(CodexProfile, [arbitraryAlias]);

        Assert.False(result.IsResolved);
        Assert.Equal(0, verifier.Calls);
        Assert.Equal(0, redirectReader.Calls);
    }

    [Theory]
    [InlineData(@"C:\Outside\current\bin")]
    [InlineData(@"C:\Users\Eric\.codex\packages\standalone\current\extra\bin")]
    [InlineData(@"C:\Users\Eric\.codex\packages\standalone\releases\0.148.0-x86_64-pc-windows-msvc\bin")]
    // feat-001/AC-18
    public void Feat001Ac18CodexAliasRejectsAnyUnexpectedInstallerBinRedirect(
        string redirectedTarget)
    {
        var pathProbe = new FakeWindowsPathProbe();
        var redirectReader = CreateExpectedCodexRedirects();
        redirectReader.Redirects[CodexAliasDirectory] = new DirectoryRedirectResolution(
            redirectedTarget,
            DirectoryRedirectFailure.None);
        var verifier = CreateCodexVerifier();
        TrustedExecutableResolver resolver = CreateCodexResolver(
            pathProbe,
            redirectReader,
            verifier);

        ExecutableResolution result = resolver.Resolve(CodexProfile, [CodexAlias]);

        Assert.False(result.IsResolved);
        Assert.Equal(0, verifier.Calls);
    }

    [Theory]
    [InlineData(@"C:\Outside\0.149.1-x86_64-pc-windows-msvc")]
    [InlineData(@"C:\Users\Eric\.codex\packages\standalone\releases\0.149.1\nested")]
    [InlineData(@"C:\Users\Eric\.codex\packages\standalone\releases")]
    // feat-001/AC-18
    public void Feat001Ac18CodexAliasRejectsOutsideNestedOrRootCurrentTargets(
        string redirectedTarget)
    {
        var pathProbe = new FakeWindowsPathProbe();
        var redirectReader = CreateExpectedCodexRedirects();
        redirectReader.Redirects[CodexCurrentDirectory] = new DirectoryRedirectResolution(
            redirectedTarget,
            DirectoryRedirectFailure.None);
        var verifier = CreateCodexVerifier();
        TrustedExecutableResolver resolver = CreateCodexResolver(
            pathProbe,
            redirectReader,
            verifier);

        ExecutableResolution result = resolver.Resolve(CodexProfile, [CodexAlias]);

        Assert.False(result.IsResolved);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18CodexAliasRejectsAReparsePointInTheFinalReleasePath()
    {
        var pathProbe = new FakeWindowsPathProbe();
        pathProbe.ReparsePaths.Add(CodexFinalExecutable);
        var redirectReader = CreateExpectedCodexRedirects();
        var verifier = CreateCodexVerifier();
        TrustedExecutableResolver resolver = CreateCodexResolver(
            pathProbe,
            redirectReader,
            verifier);

        ExecutableResolution result = resolver.Resolve(CodexProfile, [CodexAlias]);

        Assert.False(result.IsResolved);
        Assert.Equal(0, verifier.Calls);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18CodexAliasRejectsSamePublisherDisplayNameWithWrongFullSigner()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var redirectReader = CreateExpectedCodexRedirects();
        var verifier = CreateCodexVerifier();
        verifier.Verification = new ExecutableTrustVerification(
            true,
            "OpenAI OpCo, LLC",
            "codex-file-id",
            SignerSubject: "CN=OpenAI OpCo, LLC, O=Different Organization, C=US");
        TrustedExecutableResolver resolver = CreateCodexResolver(
            pathProbe,
            redirectReader,
            verifier);

        ExecutableResolution result = resolver.Resolve(CodexProfile, [CodexAlias]);

        Assert.False(result.IsResolved);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18CodexAliasRetargetBeforeDispatchFailsClosed()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var redirectReader = CreateExpectedCodexRedirects();
        var verifier = CreateCodexVerifier();
        TrustedExecutableResolver resolver = CreateCodexResolver(
            pathProbe,
            redirectReader,
            verifier);
        ResolvedExecutable initial = resolver.Resolve(CodexProfile, [CodexAlias]).Executable!;
        redirectReader.Redirects[CodexCurrentDirectory] = new DirectoryRedirectResolution(
            @"C:\Users\Eric\.codex\packages\standalone\releases\0.150.0-x86_64-pc-windows-msvc",
            DirectoryRedirectFailure.None);

        bool result = resolver.Revalidate(initial);

        Assert.False(result);
        Assert.Equal([CodexFinalExecutable], verifier.Paths);
    }

    [Fact]
    // feat-001/AC-18
    public void Feat001Ac18CodexAliasDispatchRevalidationRepeatsTrustOnTheFinalPath()
    {
        var pathProbe = new FakeWindowsPathProbe();
        var redirectReader = CreateExpectedCodexRedirects();
        var verifier = CreateCodexVerifier();
        TrustedExecutableResolver resolver = CreateCodexResolver(
            pathProbe,
            redirectReader,
            verifier);
        ResolvedExecutable initial = resolver.Resolve(CodexProfile, [CodexAlias]).Executable!;

        bool result = resolver.Revalidate(initial);

        Assert.True(result);
        Assert.Equal([CodexFinalExecutable, CodexFinalExecutable], verifier.Paths);
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

    private static FakeDirectoryRedirectReader CreateExpectedCodexRedirects()
    {
        var reader = new FakeDirectoryRedirectReader();
        reader.Redirects[CodexAliasDirectory] = new DirectoryRedirectResolution(
            CodexCurrentBin,
            DirectoryRedirectFailure.None);
        reader.Redirects[CodexCurrentDirectory] = new DirectoryRedirectResolution(
            CodexRelease,
            DirectoryRedirectFailure.None);
        return reader;
    }

    private static FakeExecutableTrustVerifier CreateCodexVerifier() => new()
    {
        Verification = new ExecutableTrustVerification(
            true,
            "OpenAI OpCo, LLC",
            "codex-file-id",
            SignerSubject: CodexSignerSubject),
    };

    private static TrustedExecutableResolver CreateCodexResolver(
        FakeWindowsPathProbe pathProbe,
        FakeDirectoryRedirectReader redirectReader,
        FakeExecutableTrustVerifier verifier)
    {
        var pathPolicy = new LocalPathPolicy(pathProbe);
        return new TrustedExecutableResolver(
            pathPolicy,
            verifier,
            new CodexInstallerAliasPolicy(
                pathPolicy,
                redirectReader,
                UserProfile,
                LocalAppData));
    }
}
