using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SessionSearch.Infrastructure.Storage;

namespace SessionSearch.IntegrationTests.Storage;

[SupportedOSPlatform("windows")]
public sealed class AppDataSecurityTests
{
    // feat-001/AC-18 feat-001/AC-19
    [Theory]
    [InlineData("relative\\SessionSearch")]
    [InlineData(@"\\attacker.example\share\SessionSearch")]
    [InlineData(@"\\?\C:\SessionSearch")]
    [InlineData(@"\??\C:\SessionSearch")]
    public void Feat001Ac18RejectsUnsafeAppDataPathsBeforeCreation(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Throws<SessionDatabaseException>(
            () => AppDataSecurity.PrepareProtectedDirectory(path));
    }

    // feat-001/AC-19
    [Fact]
    public void Feat001Ac19AppliesExactProtectedDirectoryDaclAndInheritedFileDacl()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TestWorkspace workspace = new();
        string protectedDirectory = Path.Combine(workspace.Root, "Protected");
        AppDataSecurity.PrepareProtectedDirectory(protectedDirectory);

        DirectorySecurity directorySecurity = new DirectoryInfo(protectedDirectory)
            .GetAccessControl(AccessControlSections.Access);
        Assert.True(directorySecurity.AreAccessRulesProtected);
        AssertExactPrincipals(directorySecurity);

        string protectedFile = Path.Combine(protectedDirectory, "fixture.tmp");
        File.WriteAllText(protectedFile, "synthetic fixture");
        AppDataSecurity.VerifyProtectedFileIfExists(protectedDirectory, protectedFile);
        AssertExactPrincipals(
            new FileInfo(protectedFile).GetAccessControl(AccessControlSections.Access));
    }

    // feat-001/AC-19
    [Fact]
    public void Feat001Ac19RejectsAProtectedFileWithAnUnexpectedPrincipal()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TestWorkspace workspace = new();
        string protectedDirectory = Path.Combine(workspace.Root, "Protected");
        AppDataSecurity.PrepareProtectedDirectory(protectedDirectory);
        string protectedFile = Path.Combine(protectedDirectory, "fixture.tmp");
        File.WriteAllText(protectedFile, "synthetic fixture");

        FileInfo file = new(protectedFile);
        FileSecurity security = file.GetAccessControl(AccessControlSections.Access);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, null),
            FileSystemRights.Read,
            AccessControlType.Allow));
        file.SetAccessControl(security);

        Assert.Throws<SessionDatabaseException>(
            () => AppDataSecurity.VerifyProtectedFileIfExists(
                protectedDirectory,
                protectedFile));
    }

    // feat-001/AC-19
    [Fact]
    public void Feat001Ac19AppliesExactProtectedDaclToStandaloneArtifact()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TestWorkspace workspace = new();
        string artifact = Path.Combine(workspace.Root, "benchmark-report.json");
        File.WriteAllText(artifact, "{}");

        AppDataSecurity.ProtectStandaloneFile(artifact);

        FileSecurity security = new FileInfo(artifact)
            .GetAccessControl(AccessControlSections.Access);
        Assert.True(security.AreAccessRulesProtected);
        AssertExactPrincipals(security);
    }

    // feat-001/AC-19
    [Fact]
    public async Task Feat001Ac19ProtectedDatabaseVerifiesItsDatabaseAndSidecars()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using TestWorkspace workspace = new();
        string protectedDirectory = Path.Combine(workspace.Root, "ProtectedDatabase");
        string databasePath = Path.Combine(protectedDirectory, "session-search.sqlite3");
        await using SessionDatabase database = await SessionDatabase.CreateAsync(
            databasePath,
            protectDirectory: true,
            TestContext.Current.CancellationToken);

        AppDataSecurity.VerifyProtectedFileIfExists(protectedDirectory, databasePath);
        AppDataSecurity.VerifyProtectedFileIfExists(protectedDirectory, databasePath + "-wal");
        AppDataSecurity.VerifyProtectedFileIfExists(protectedDirectory, databasePath + "-shm");
    }

    private static void AssertExactPrincipals(FileSystemSecurity security)
    {
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User!;
        HashSet<string> expected =
        [
            currentUser.Value,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
        ];
        HashSet<string> actual = [];
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier)))
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights & FileSystemRights.FullControl);
            actual.Add(((SecurityIdentifier)rule.IdentityReference).Value);
        }

        Assert.True(actual.SetEquals(expected));
    }
}
