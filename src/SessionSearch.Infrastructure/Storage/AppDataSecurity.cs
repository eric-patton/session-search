using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.Infrastructure.Storage;

public static class AppDataSecurity
{
    [SupportedOSPlatform("windows")]
    public static void PrepareProtectedDirectory(string directoryPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected app storage requires Windows.");
        }

        LocalPathValidation lexical = LocalPathPolicy.ValidateLexically(directoryPath);
        if (!lexical.IsSafe)
        {
            throw new SessionDatabaseException(
                $"The app-data directory is unsafe: {lexical.Reason}");
        }

        string fullPath = lexical.CanonicalPath!;
        DriveInfo drive = new(Path.GetPathRoot(fullPath)!);
        if (drive.DriveType != DriveType.Fixed)
        {
            throw new SessionDatabaseException("The app-data directory must be on a fixed local drive.");
        }

        RejectExistingReparseComponents(fullPath);
        DirectoryInfo directory = Directory.CreateDirectory(fullPath);
        LocalPathValidation physical = new LocalPathPolicy(new PhysicalWindowsPathProbe())
            .ValidateExistingDirectory(fullPath);
        if (!physical.IsSafe ||
            !string.Equals(physical.CanonicalPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionDatabaseException(
                "The app-data directory does not resolve to its trusted local path.");
        }

        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new SessionDatabaseException("The current Windows user SID is unavailable.");
        SecurityIdentifier localSystem = new(WellKnownSidType.LocalSystemSid, null);
        SecurityIdentifier administrators = new(
            WellKnownSidType.BuiltinAdministratorsSid,
            null);

        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        AddFullControl(security, currentUser);
        AddFullControl(security, localSystem);
        AddFullControl(security, administrators);
        directory.SetAccessControl(security);

        VerifyProtectedDirectory(directory, currentUser, localSystem, administrators);
    }

    [SupportedOSPlatform("windows")]
    public static void VerifyProtectedFileIfExists(
        string protectedDirectoryPath,
        string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected app storage requires Windows.");
        }

        LocalPathValidation rootLexical = LocalPathPolicy.ValidateLexically(protectedDirectoryPath);
        LocalPathValidation fileLexical = LocalPathPolicy.ValidateLexically(filePath);
        if (!rootLexical.IsSafe || !fileLexical.IsSafe)
        {
            throw new SessionDatabaseException("A protected app-data file path is unsafe.");
        }

        string root = rootLexical.CanonicalPath!;
        string file = fileLexical.CanonicalPath!;
        if (!IsWithinRoot(file, root))
        {
            throw new SessionDatabaseException("A protected app-data file escapes its trusted root.");
        }

        DriveInfo drive = new(Path.GetPathRoot(file)!);
        if (drive.DriveType != DriveType.Fixed)
        {
            throw new SessionDatabaseException("A protected app-data file must be on a fixed local drive.");
        }

        if (!File.Exists(file))
        {
            return;
        }

        LocalPathValidation physical = new LocalPathPolicy(new PhysicalWindowsPathProbe())
            .ValidateExistingFile(file, Path.GetFileName(file), trustedRoot: root);
        if (!physical.IsSafe ||
            !string.Equals(physical.CanonicalPath, file, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionDatabaseException(
                "A protected app-data file does not resolve to its trusted local path.");
        }

        SecurityIdentifier[] expectedIdentities = GetExpectedIdentities();
        DirectoryInfo directory = new(root);
        VerifyProtectedDirectory(directory, expectedIdentities);
        FileSecurity security = new FileInfo(file).GetAccessControl(AccessControlSections.Access);
        VerifyAccessRules(security, expectedIdentities);
    }

    [SupportedOSPlatform("windows")]
    public static void ProtectStandaloneFile(string filePath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Protected app storage requires Windows.");
        }

        LocalPathValidation lexical = LocalPathPolicy.ValidateLexically(filePath);
        if (!lexical.IsSafe)
        {
            throw new SessionDatabaseException("The protected file path is unsafe.");
        }

        string fullPath = lexical.CanonicalPath!;
        if (!File.Exists(fullPath))
        {
            throw new SessionDatabaseException("The protected file does not exist.");
        }

        DriveInfo drive = new(Path.GetPathRoot(fullPath)!);
        if (drive.DriveType != DriveType.Fixed)
        {
            throw new SessionDatabaseException("A protected file must be on a fixed local drive.");
        }

        RejectExistingReparseComponents(fullPath);
        string parent = Path.GetDirectoryName(fullPath)
            ?? throw new SessionDatabaseException("The protected file has no parent directory.");
        LocalPathValidation physical = new LocalPathPolicy(new PhysicalWindowsPathProbe())
            .ValidateExistingFile(fullPath, Path.GetFileName(fullPath), trustedRoot: parent);
        if (!physical.IsSafe ||
            !string.Equals(physical.CanonicalPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new SessionDatabaseException(
                "The protected file does not resolve to its trusted local path.");
        }

        SecurityIdentifier[] expectedIdentities = GetExpectedIdentities();
        FileSecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (SecurityIdentifier identity in expectedIdentities)
        {
            security.AddAccessRule(new FileSystemAccessRule(
                identity,
                FileSystemRights.FullControl,
                AccessControlType.Allow));
        }

        FileInfo file = new(fullPath);
        file.SetAccessControl(security);
        VerifyAccessRules(
            file.GetAccessControl(AccessControlSections.Access),
            expectedIdentities);
    }

    [SupportedOSPlatform("windows")]
    private static void AddFullControl(
        DirectorySecurity security,
        SecurityIdentifier identity)
    {
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyProtectedDirectory(
        DirectoryInfo directory,
        params SecurityIdentifier[] expectedIdentities)
    {
        DirectorySecurity applied = directory.GetAccessControl(AccessControlSections.Access);
        if (!applied.AreAccessRulesProtected)
        {
            throw new SessionDatabaseException("The app-data DACL is not protected.");
        }

        VerifyAccessRules(applied, expectedIdentities);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyAccessRules(
        FileSystemSecurity security,
        IReadOnlyCollection<SecurityIdentifier> expectedIdentities)
    {
        HashSet<string> expected = expectedIdentities
            .Select(identity => identity.Value)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> present = new(StringComparer.Ordinal);
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier)))
        {
            string sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (!expected.Contains(sid)
                || rule.AccessControlType != AccessControlType.Allow
                || (rule.FileSystemRights & FileSystemRights.FullControl) == 0)
            {
                throw new SessionDatabaseException("The app-data DACL contains an unexpected rule.");
            }

            present.Add(sid);
        }

        if (!present.SetEquals(expected))
        {
            throw new SessionDatabaseException("The app-data DACL is missing a required identity.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier[] GetExpectedIdentities()
    {
        SecurityIdentifier currentUser = WindowsIdentity.GetCurrent().User
            ?? throw new SessionDatabaseException("The current Windows user SID is unavailable.");
        return
        [
            currentUser,
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
        ];
    }

    private static void RejectExistingReparseComponents(string path)
    {
        string root = Path.GetPathRoot(path)!;
        string current = root;
        string relative = Path.GetRelativePath(root, path);
        foreach (string component in relative.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!Directory.Exists(current) && !File.Exists(current))
            {
                break;
            }

            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new SessionDatabaseException(
                    "The app-data directory cannot cross a reparse point.");
            }
        }
    }

    private static bool IsWithinRoot(string path, string root)
    {
        string normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        return string.Equals(path, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }
}
