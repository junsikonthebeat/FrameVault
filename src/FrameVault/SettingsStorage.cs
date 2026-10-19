using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;

namespace FrameVault;

public sealed class SettingsStorage
{
    private static readonly byte[] OptionalEntropy = "FrameVault.Settings.v1"u8.ToArray();
    // 이전 제품명의 문자열을 노출하지 않고 기존 DPAPI 설정을 한 번만 이전한다.
    private static readonly byte[] LegacyEntropy =
        [78, 105, 107, 111, 110, 80, 104, 111, 116, 111, 66, 97, 99, 107, 117, 112, 46, 83, 101, 116, 116, 105, 110, 103, 115, 46, 118, 50];
    private readonly string _path;
    private readonly string _legacyPath;

    public SettingsStorage()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(local, "FrameVault");
        Directory.CreateDirectory(folder);
        _path = Path.Combine(folder, "settings.protected");
        var legacyFolderName = new string(new[]
        {
            (char)78, (char)105, (char)107, (char)111, (char)110, (char)80, (char)104, (char)111,
            (char)116, (char)111, (char)66, (char)97, (char)99, (char)107, (char)117, (char)112
        });
        _legacyPath = Path.Combine(local, legacyFolderName, "settings.protected");
    }

    public ProtectedSettingsDocument Load()
    {
        var sourcePath = File.Exists(_path) ? _path : File.Exists(_legacyPath) ? _legacyPath : null;
        if (sourcePath is null) return new(4, [], 0);
        RestrictFileAccess(sourcePath);
        try
        {
            var encrypted = File.ReadAllBytes(sourcePath);
            byte[] bytes;
            try
            {
                var entropy = string.Equals(sourcePath, _legacyPath, StringComparison.OrdinalIgnoreCase)
                    ? LegacyEntropy
                    : OptionalEntropy;
                bytes = ProtectedData.Unprotect(encrypted, entropy, DataProtectionScope.CurrentUser);
            }
            catch (CryptographicException)
            {
                try { bytes = ProtectedData.Unprotect(encrypted, LegacyEntropy, DataProtectionScope.CurrentUser); }
                catch (CryptographicException) { bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser); }
            }

            try
            {
                var document = JsonSerializer.Deserialize<ProtectedSettingsDocument>(bytes);
                if (document is { Version: >= 2, Profiles: not null })
                {
                    var normalized = Normalize(document);
                    MigrateIfNeeded(sourcePath, normalized);
                    return normalized;
                }

                var legacy = JsonSerializer.Deserialize<BackupSettings>(bytes);
                var migrated = legacy is null
                    ? new ProtectedSettingsDocument(4, [], 0)
                    : new ProtectedSettingsDocument(4, [new("기본 프로필", legacy)], 0, legacy.CacheFolder, legacy.AutoDetect, legacy.CacheLimitGb);
                MigrateIfNeeded(sourcePath, migrated);
                return migrated;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or IOException) { return new(2, [], 0); }
    }

    public void Save(ProtectedSettingsDocument document)
    {
        var normalized = Normalize(document);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized);
        try
        {
            var encrypted = ProtectedData.Protect(bytes, OptionalEntropy, DataProtectionScope.CurrentUser);
            var temporary = _path + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            File.Move(temporary, _path, true);
            RestrictFileAccess(_path);
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private void MigrateIfNeeded(string sourcePath, ProtectedSettingsDocument document)
    {
        if (string.Equals(sourcePath, _path, StringComparison.OrdinalIgnoreCase)) return;
        try { Save(document); }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException) { }
    }

    private static void RestrictFileAccess(string path)
    {
        try
        {
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is null) return;
            var security = new FileSecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(currentUser, FileSystemRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(security);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            // DPAPI 암호화는 유지되며 ACL 강화만 생략한다.
        }
    }
    private static ProtectedSettingsDocument Normalize(ProtectedSettingsDocument document)
    {
        var profiles = (document.Profiles ?? []).Take(4).ToList();
        var active = profiles.Count == 0 ? 0 : Math.Clamp(document.ActiveProfileIndex, 0, profiles.Count - 1);
        var legacy = profiles.Count == 0 ? null : profiles[active].Settings;
        var cacheFolder = document.Version >= 3 ? document.CacheFolder : legacy?.CacheFolder;
        var autoDetect = document.Version >= 3 ? document.AutoDetect : legacy?.AutoDetect ?? true;
        var cacheLimit = document.Version >= 3 ? document.CacheLimitGb : legacy?.CacheLimitGb ?? 50;
        var theme = document.Version >= 4 && string.Equals(document.Theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        return new(4, profiles, active, cacheFolder, autoDetect, cacheLimit <= 0 ? 50 : cacheLimit, theme);
    }
}



