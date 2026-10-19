using System.Security.Cryptography;
using System.Text;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using Windows.Storage;

namespace FrameVault;

public enum CameraSourceKind { Folder, Wpd }

public sealed record CameraSource(CameraSourceKind Kind, string Id, string DisplayName, string MediaRootName)
{
    public string SettingsValue => Kind == CameraSourceKind.Wpd ? $"wpd:{Id}" : Id;
}

public static class CameraSourceDiscovery
{
    internal static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".nef", ".arw", ".cr2", ".cr3", ".raf", ".orf", ".rw2", ".heic", ".mp4", ".mov" };

    public static async Task<HashSet<string>> GetConnectedDeviceKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (drive.IsReady && drive.DriveType is DriveType.Removable or DriveType.Fixed &&
                    Directory.Exists(Path.Combine(drive.RootDirectory.FullName, "DCIM")))
                    keys.Add("drive:" + drive.RootDirectory.FullName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        foreach (var device in await FindPresentStorageDevicesAsync())
            keys.Add("wpd:" + device.Id);
        return keys;
    }
    public static async Task<IReadOnlyList<CameraSource>> FindAsync(CancellationToken ct)
    {
        var folderSources = new List<CameraSource>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Removable or DriveType.Fixed)) continue;
                var dcim = Directory.EnumerateDirectories(drive.RootDirectory.FullName, "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(path => string.Equals(Path.GetFileName(path), "DCIM", StringComparison.OrdinalIgnoreCase));
                if (dcim is null || !Directory.EnumerateFiles(dcim, "*", SearchOption.AllDirectories).Any(IsMediaFile)) continue;
                var label = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\')})";
                folderSources.Add(new(CameraSourceKind.Folder, dcim, label, "DCIM"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var wpdSources = new List<CameraSource>();
        foreach (var device in await FindPresentStorageDevicesAsync())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var mediaRoot = await FindMediaRootAsync(StorageDevice.FromId(device.Id), ct);
                if (mediaRoot is not null) wpdSources.Add(new(CameraSourceKind.Wpd, device.Id, device.Name, mediaRoot.Name));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }

        // 직접 접근 가능한 USB/SD 드라이브를 먼저 제시한다. 이 경로가 백업 중에도 가장 안정적이다.
        return folderSources.Concat(wpdSources).ToList();
    }
    private static async Task<IReadOnlyList<DeviceInformation>> FindPresentStorageDevicesAsync()
    {
        string[] properties = ["System.Devices.Present", "System.Devices.InterfaceEnabled"];
        var devices = await DeviceInformation.FindAllAsync(StorageDevice.GetDeviceSelector(), properties);
        return devices.Where(device => PropertyIsTrueOrUnavailable(device, "System.Devices.Present") &&
                                       PropertyIsTrueOrUnavailable(device, "System.Devices.InterfaceEnabled")).ToList();
    }

    private static bool PropertyIsTrueOrUnavailable(DeviceInformation device, string name) =>
        !device.Properties.TryGetValue(name, out var value) || value is not bool present || present;
    public static bool IsWpd(string value) => value.StartsWith("wpd:", StringComparison.OrdinalIgnoreCase);
    public static string WpdId(string value) => value[4..];

    public static Task<PhotoCandidate?> FindLatestPhotoAsync(string source, CancellationToken ct) =>
        IsWpd(source) ? FindLatestWpdPhotoAsync(WpdId(source), ct) : Task.Run(() => FindLatestFolderPhoto(source, ct), ct);

    private static PhotoCandidate? FindLatestFolderPhoto(string source, CancellationToken ct)
    {
        PhotoCandidate? latest = null;
        foreach (var path in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsPhotoFile(path)) continue;
            var info = new FileInfo(path);
            var candidate = new PhotoCandidate(path, Path.GetRelativePath(source, path), info.Length,
                info.LastWriteTimeUtc, info.LastWriteTime, "folder");
            if (IsNewerPhoto(candidate, latest)) latest = candidate;
        }
        return latest;
    }

    private static async Task<PhotoCandidate?> FindLatestWpdPhotoAsync(string deviceId, CancellationToken ct)
    {
        var mediaRoot = await FindMediaRootAsync(StorageDevice.FromId(deviceId), ct)
            ?? throw new DirectoryNotFoundException("Camera storage has no DCIM folder.");
        var sourceId = "wpd:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)))[..20];
        return await FindLatestWpdPhotoInFolderAsync(mediaRoot, mediaRoot.Name, sourceId, ct);
    }

    private static async Task<PhotoCandidate?> FindLatestWpdPhotoInFolderAsync(
        StorageFolder folder, string relativeFolder, string sourceId, CancellationToken ct)
    {
        PhotoCandidate? latest = null;
        foreach (var file in await folder.GetFilesAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (!IsPhotoFile(file.Name)) continue;
            var properties = await file.GetBasicPropertiesAsync();
            Func<CancellationToken, Task<Stream>> openSource = async token =>
            {
                token.ThrowIfCancellationRequested();
                var randomAccess = await file.OpenReadAsync();
                return randomAccess.AsStreamForRead();
            };
            var modifiedUtc = properties.DateModified.UtcDateTime;
            var candidate = new PhotoCandidate(file.Name, Path.Combine(relativeFolder, file.Name),
                (long)properties.Size, modifiedUtc, properties.DateModified.LocalDateTime,
                sourceId, false, openSource);
            if (IsNewerPhoto(candidate, latest)) latest = candidate;
        }
        foreach (var child in await folder.GetFoldersAsync())
        {
            ct.ThrowIfCancellationRequested();
            var candidate = await FindLatestWpdPhotoInFolderAsync(
                child, Path.Combine(relativeFolder, child.Name), sourceId, ct);
            if (candidate is not null && IsNewerPhoto(candidate, latest)) latest = candidate;
        }
        return latest;
    }

    private static bool IsNewerPhoto(PhotoCandidate candidate, PhotoCandidate? current) =>
        current is null || candidate.LastWriteUtc > current.LastWriteUtc ||
        candidate.LastWriteUtc == current.LastWriteUtc &&
        StringComparer.OrdinalIgnoreCase.Compare(candidate.FileName, current.FileName) > 0;

    private static bool IsPhotoFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".nef", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".arw", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cr2", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".cr3", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".raf", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".orf", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".rw2", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".heic", StringComparison.OrdinalIgnoreCase);
    }
    public static async Task<IReadOnlyList<PhotoCandidate>> ScanWpdAsync(string deviceId, IProgress<string>? progress, CancellationToken ct)
    {
        var mediaRoot = await FindMediaRootAsync(StorageDevice.FromId(deviceId), ct)
            ?? throw new DirectoryNotFoundException("Camera storage has no DCIM folder.");
        var sourceId = "wpd:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(deviceId)))[..20];
        var candidates = new List<PhotoCandidate>();
        await EnumerateWpdAsync(mediaRoot, mediaRoot.Name, sourceId, candidates, progress, ct);
        return candidates.OrderBy(item => item.TakenAt).ToList();
    }

    private static async Task EnumerateWpdAsync(StorageFolder folder, string relativeFolder, string sourceId,
        List<PhotoCandidate> result, IProgress<string>? progress, CancellationToken ct)
    {
        foreach (var file in await folder.GetFilesAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (!Extensions.Contains(file.FileType)) continue;
            progress?.Report($"카메라 파일 정보 읽는 중: {file.Name}");
            var properties = await file.GetBasicPropertiesAsync();
            Func<CancellationToken, Task<Stream>> openSource = async token =>
            {
                token.ThrowIfCancellationRequested();
                var randomAccess = await file.OpenReadAsync();
                return randomAccess.AsStreamForRead();
            };
            DateTime? takenAt;
            await using (var stream = await openSource(ct))
                takenAt = await Task.Run(() => PhotoScanner.ReadExifDate(stream), ct);
            result.Add(new PhotoCandidate(file.Name, Path.Combine(relativeFolder, file.Name),
                (long)properties.Size, properties.DateModified.UtcDateTime,
                takenAt ?? properties.DateModified.LocalDateTime, sourceId, false, openSource));
        }
        foreach (var child in await folder.GetFoldersAsync())
        {
            ct.ThrowIfCancellationRequested();
            await EnumerateWpdAsync(child, Path.Combine(relativeFolder, child.Name), sourceId, result, progress, ct);
        }
    }

    private static async Task<StorageFolder?> FindMediaRootAsync(StorageFolder root, CancellationToken ct)
    {
        foreach (var storage in await root.GetFoldersAsync())
        {
            var found = await FindFolderAsync(storage, "DCIM", 2, ct);
            if (found is not null && await ContainsMediaAsync(found, ct)) return found;
        }
        return null;
    }

    private static async Task<StorageFolder?> FindFolderAsync(StorageFolder folder, string name, int depth, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase)) return folder;
        if (depth == 0) return null;
        foreach (var child in await folder.GetFoldersAsync())
        {
            var found = await FindFolderAsync(child, name, depth - 1, ct);
            if (found is not null) return found;
        }
        return null;
    }

    private static async Task<bool> ContainsMediaAsync(StorageFolder folder, CancellationToken ct)
    {
        if ((await folder.GetFilesAsync()).Any(file => Extensions.Contains(file.FileType))) return true;
        foreach (var child in await folder.GetFoldersAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (await ContainsMediaAsync(child, ct)) return true;
        }
        return false;
    }

    private static bool IsMediaFile(string path) => Extensions.Contains(Path.GetExtension(path));

    private static void EnsureFreeSpace(string path, long required)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (root is null) return;
        var drive = new DriveInfo(root);
        var reserve = Math.Max(512L * 1024 * 1024, required / 10);
        if (drive.AvailableFreeSpace < required + reserve)
            throw new IOException($"로컬 캐시 공간이 부족합니다. 최소 {required + reserve:N0}바이트가 필요합니다.");
    }
}

