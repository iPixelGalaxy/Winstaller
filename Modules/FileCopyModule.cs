using System.Security.AccessControl;
using System.Security.Principal;
using Winstaller.Configuration;
using Winstaller.Utilities;

namespace Winstaller.Modules;

public class FileCopyModule : ModuleBase
{
    public FileCopyModule(WinstallerConfig config) : base(config) { }
    public override string Name => "Files & Shortcuts";
    public override string Description => "Copies configured files, shortcuts, and private keys";
    public override bool IsEnabled => Config.FileCopy.Enabled;

    public override Task<bool> ExecuteAsync()
    {
        if (!IsEnabled) return Task.FromResult(false);
        var success = true;
        foreach (var operation in Config.FileCopy.Operations)
        {
            try { Copy(operation); }
            catch (Exception ex) { ConsoleHelper.WriteError($"{OperationName(operation)}: {ex.Message}"); success = false; }
        }
        return Task.FromResult(success);
    }

    private static void Copy(Models.FileCopyOperation operation)
    {
        var source = Expand(operation.Source);
        var destination = Expand(operation.Destination);
        var files = operation.MatchingFiles
            ? Directory.Exists(source) ? Directory.GetFiles(source, string.IsNullOrWhiteSpace(operation.SearchPattern) ? "*" : operation.SearchPattern) : []
            : File.Exists(source) ? [source] : [];
        if (files.Length == 0) throw new FileNotFoundException("Source file(s) not found", source);

        foreach (var file in files)
        {
            var target = operation.MatchingFiles || Directory.Exists(destination) || destination.EndsWith(Path.DirectorySeparatorChar.ToString())
                ? Path.Combine(destination, Path.GetFileName(file)) : destination;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
            if (operation.RewriteShortcutProfilePaths && target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) RewriteShortcut(target);
            if (operation.ProtectPrivateKeyAcl) ProtectPrivateKey(target);
            ConsoleHelper.WriteSuccess($"Copied {Path.GetFileName(file)}");
        }
    }

    private static string Expand(string value) => Environment.ExpandEnvironmentVariables(value).Replace("{USERNAME}", Environment.UserName);
    private static string OperationName(Models.FileCopyOperation operation) => string.IsNullOrWhiteSpace(operation.Name) ? operation.Source : operation.Name;
    private static void RewriteShortcut(string path)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        var shell = type is null ? null : Activator.CreateInstance(type);
        if (shell is null) return;
        dynamic shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [path]);
        var oldProfile = @"C:\Users\iPixelGalaxy";
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        shortcut.TargetPath = ((string)shortcut.TargetPath).Replace(oldProfile, profile, StringComparison.OrdinalIgnoreCase);
        shortcut.WorkingDirectory = ((string)shortcut.WorkingDirectory).Replace(oldProfile, profile, StringComparison.OrdinalIgnoreCase);
        shortcut.IconLocation = ((string)shortcut.IconLocation).Replace(oldProfile, profile, StringComparison.OrdinalIgnoreCase);
        shortcut.Arguments = ((string)shortcut.Arguments).Replace(oldProfile, profile, StringComparison.OrdinalIgnoreCase);
        shortcut.Save();
    }

    public static void ProtectPrivateKey(string path)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
}
