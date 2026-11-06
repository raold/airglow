using System.Security.AccessControl;
using System.Security.Principal;

namespace Airglow.Service.Tests;

public sealed class ConfigDirectoryTests : IDisposable
{
    private const FileSystemRights WriteRights =
        FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    private static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    private readonly string _root = Directory.CreateTempSubdirectory("airglow-acl-").FullName;
    private readonly string _directory;

    public ConfigDirectoryTests()
    {
        _directory = Path.Combine(_root, "Airglow");
        var root = new DirectoryInfo(_root);
        var security = root.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.Write,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        root.SetAccessControl(security);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory) && WindowsIdentity.GetCurrent().User is { } self)
        {
            var open = new DirectorySecurity();
            open.AddAccessRule(new FileSystemAccessRule(self, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(_directory).SetAccessControl(open);
        }
        Directory.Delete(_root, recursive: true);
    }

    private static List<FileSystemAccessRule> Rules(FileSystemSecurity security) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();

    [Fact]
    public void Secure_creates_the_directory_with_a_protected_three_rule_acl()
    {
        ConfigDirectory.Secure(_directory);

        var security = new DirectoryInfo(_directory).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        var rules = Rules(security);
        Assert.All(rules, r => Assert.False(r.IsInherited));
        Assert.All(rules, r => Assert.Equal(AccessControlType.Allow, r.AccessControlType));
        Assert.Equal(3, rules.Count);

        FileSystemRights RightsOf(WellKnownSidType sid) =>
            Assert.Single(rules, r => r.IdentityReference.Equals(new SecurityIdentifier(sid, null))).FileSystemRights;
        Assert.Equal(FileSystemRights.FullControl, RightsOf(WellKnownSidType.LocalSystemSid));
        Assert.Equal(FileSystemRights.FullControl, RightsOf(WellKnownSidType.BuiltinAdministratorsSid));
        var users = RightsOf(WellKnownSidType.BuiltinUsersSid);
        Assert.Equal(FileSystemRights.ReadAndExecute, users & FileSystemRights.ReadAndExecute);
        Assert.Equal((FileSystemRights)0, users & WriteRights);
    }

    [Fact]
    public void Secure_replaces_inherited_user_write_access_on_an_existing_directory_and_file()
    {
        Directory.CreateDirectory(_directory);
        var file = Path.Combine(_directory, "config.json");
        File.WriteAllText(file, "{}");
        Assert.Contains(Rules(new DirectoryInfo(_directory).GetAccessControl()),
            r => r.IdentityReference.Equals(Users) && (r.FileSystemRights & WriteRights) != 0);

        ConfigDirectory.Secure(_directory);

        Assert.DoesNotContain(Rules(new DirectoryInfo(_directory).GetAccessControl()),
            r => r.IdentityReference.Equals(Users) && (r.FileSystemRights & WriteRights) != 0);
        Assert.DoesNotContain(Rules(new FileInfo(file).GetAccessControl()),
            r => r.IdentityReference.Equals(Users) && (r.FileSystemRights & WriteRights) != 0);
    }
}
