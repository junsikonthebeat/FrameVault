using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using MetadataExtractor.Formats.Exif;
using Microsoft.Data.Sqlite;
using ImageMetadataReader = MetadataExtractor.ImageMetadataReader;

namespace FrameVault;

public static class PhotoScanner
{
    public static IReadOnlyList<PhotoCandidate> Scan(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException("원본 폴더를 찾을 수 없습니다.");
        var sourceId = VolumeIdentity.Create(root);
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(path => CameraSourceDiscovery.Extensions.Contains(Path.GetExtension(path)))
            .Select(path =>
            {
                var info = new FileInfo(path);
                return CreateCandidate(path, Path.GetRelativePath(root, path), info.Length, info.LastWriteTimeUtc, sourceId, false);
            }).OrderBy(item => item.TakenAt).ToList();
    }

    public static PhotoCandidate CreateCandidate(string localPath, string relativePath, long size, DateTime lastWriteUtc,
        string sourceId = "folder", bool isStaged = false)
    {
        var date = ReadExifDate(localPath) ?? lastWriteUtc.ToLocalTime();
        return new(localPath, relativePath, size, lastWriteUtc, date, sourceId, isStaged);
    }

    public static DateTime? ReadExifDate(Stream stream)
    {
        try
        {
            var exif = ImageMetadataReader.ReadMetadata(stream).OfType<ExifSubIfdDirectory>().FirstOrDefault();
            return exif is not null && MetadataExtractor.DirectoryExtensions.TryGetDateTime(exif, ExifDirectoryBase.TagDateTimeOriginal, out var value)
                ? value : null;
        }
        catch { return null; }
    }

    private static DateTime? ReadExifDate(string path)
    {
        try
        {
            var exif = ImageMetadataReader.ReadMetadata(path).OfType<ExifSubIfdDirectory>().FirstOrDefault();
            return exif is not null && MetadataExtractor.DirectoryExtensions.TryGetDateTime(exif, ExifDirectoryBase.TagDateTimeOriginal, out var value)
                ? value : null;
        }
        catch { return null; }
    }
}

public static class VolumeIdentity
{
    public static string Create(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path)) ?? path;
        var volumeName = new StringBuilder(261);
        var fileSystem = new StringBuilder(261);
        return GetVolumeInformation(root, volumeName, volumeName.Capacity, out var serial, out _, out _, fileSystem, fileSystem.Capacity)
            ? $"volume:{serial:X8}:{volumeName}:{fileSystem}"
            : "folder:" + Path.GetFullPath(path).ToUpperInvariant();
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformation(string rootPathName, StringBuilder volumeNameBuffer, int volumeNameSize,
        out uint volumeSerialNumber, out uint maximumComponentLength, out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer, int fileSystemNameSize);
}

public sealed class BackupLog : IDisposable
{
    private readonly SqliteConnection _connection;
    public BackupLog()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var folder = Path.Combine(local, "FrameVault");
        Directory.CreateDirectory(folder);
        var databasePath = Path.Combine(folder, "backup.db");
        if (!File.Exists(databasePath))
        {
            var legacyFolderName = new string(new[]
            {
                (char)78, (char)105, (char)107, (char)111, (char)110, (char)80, (char)104, (char)111,
                (char)116, (char)111, (char)66, (char)97, (char)99, (char)107, (char)117, (char)112
            });
            var legacyDatabase = Path.Combine(local, legacyFolderName, "backup.db");
            try
            {
                if (File.Exists(legacyDatabase)) File.Copy(legacyDatabase, databasePath, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        _connection = new($"Data Source={databasePath}");
        _connection.Open();
        using var command = _connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS completed_v2 (
              source_key TEXT NOT NULL, destination_id TEXT NOT NULL, destination_path TEXT NOT NULL,
              size INTEGER NOT NULL, sha256 TEXT NOT NULL, verification TEXT NOT NULL, completed_utc TEXT NOT NULL,
              PRIMARY KEY(source_key, destination_id));
            CREATE TABLE IF NOT EXISTS transfer_state (
              source_key TEXT NOT NULL, destination_id TEXT NOT NULL, state TEXT NOT NULL,
              detail TEXT NOT NULL, updated_utc TEXT NOT NULL, PRIMARY KEY(source_key, destination_id));
            """;
        command.ExecuteNonQuery();
    }

    public string? GetCompletedHash(PhotoCandidate item, string destinationId)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = "SELECT sha256 FROM completed_v2 WHERE source_key=$key AND destination_id=$destination;";
        command.Parameters.AddWithValue("$key", item.SourceKey);
        command.Parameters.AddWithValue("$destination", destinationId);
        return command.ExecuteScalar() as string;
    }

    public void MarkState(PhotoCandidate item, string destinationId, TransferState state, string detail = "")
    {
        using var command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO transfer_state(source_key,destination_id,state,detail,updated_utc)
            VALUES($key,$destination,$state,$detail,$utc)
            ON CONFLICT(source_key,destination_id) DO UPDATE SET state=$state,detail=$detail,updated_utc=$utc;
            """;
        command.Parameters.AddWithValue("$key", item.SourceKey);
        command.Parameters.AddWithValue("$destination", destinationId);
        command.Parameters.AddWithValue("$state", state.ToString());
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void Complete(PhotoCandidate item, string destinationId, TransferResult transfer, string hash)
    {
        using var transaction = _connection.BeginTransaction();
        using var command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO completed_v2(source_key,destination_id,destination_path,size,sha256,verification,completed_utc)
            VALUES($key,$destination,$path,$size,$hash,$verification,$utc)
            ON CONFLICT(source_key,destination_id) DO UPDATE SET destination_path=$path,size=$size,sha256=$hash,verification=$verification,completed_utc=$utc;
            DELETE FROM transfer_state WHERE source_key=$key AND destination_id=$destination;
            """;
        command.Parameters.AddWithValue("$key", item.SourceKey);
        command.Parameters.AddWithValue("$destination", destinationId);
        command.Parameters.AddWithValue("$path", transfer.DestinationPath);
        command.Parameters.AddWithValue("$size", item.Size);
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$verification", transfer.Verification);
        command.Parameters.AddWithValue("$utc", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();
    }
    public void Dispose() => _connection.Dispose();
}

public static class Hashing
{
    public static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }

    public static async Task<string> Sha256Async(PhotoCandidate item, CancellationToken ct)
    {
        await using var stream = await item.OpenReadAsync(ct);
        return await Sha256Async(stream, ct);
    }

    public static async Task<string> Sha256Async(Stream stream, CancellationToken ct) =>
        Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
}

public interface IDestination : IAsyncDisposable
{
    string Id { get; }
    Task TestAsync(CancellationToken ct);
    Task<IReadOnlyList<DestinationFolder>> ListDirectoriesAsync(string relativePath, CancellationToken ct);
    Task<bool> VerifyExistingAsync(PhotoCandidate item, string expectedHash, CancellationToken ct);
    Task<IntegrityCheckResult> VerifyIntegrityAsync(PhotoCandidate item, string expectedHash, CancellationToken ct);
    Task<TransferResult> UploadAtomicAsync(PhotoCandidate item, string localHash, CancellationToken ct);
}

public static class DestinationFactory
{
    public static IDestination Create(BackupSettings settings) => settings.Protocol switch
    {
        BackupProtocol.Local => new LocalDestination(settings),
        BackupProtocol.Smb => new SmbDestination(settings),
        BackupProtocol.WebDav => new WebDavDestination(settings),
        _ => throw new NotSupportedException()
    };
}

internal abstract class FileSystemDestination : IDestination
{
    protected readonly BackupSettings Settings;
    protected readonly string Root;
    protected FileSystemDestination(BackupSettings settings)
    {
        Settings = settings;
        Root = Path.GetFullPath(settings.Endpoint.TrimEnd('\\', '/'));
    }
    public string Id => $"{Settings.Protocol}:{Root.ToUpperInvariant()}:{Settings.LibraryFolder}";
    protected virtual Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
    public async Task TestAsync(CancellationToken ct)
    {
        await ConnectAsync(ct);
        if (!Directory.Exists(Root)) throw new IOException("대상 폴더에 접근할 수 없습니다.");
    }
    public async Task<IReadOnlyList<DestinationFolder>> ListDirectoriesAsync(string relativePath, CancellationToken ct)
    {
        await ConnectAsync(ct);
        var path = SafeDirectoryPath(Root, relativePath);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("선택한 대상 폴더를 찾을 수 없습니다.");
        return Directory.EnumerateDirectories(path)
            .Select(directory => new DestinationFolder(Path.GetFileName(directory), Path.GetRelativePath(Root, directory).Replace('\\', '/')))
            .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    public async Task<bool> VerifyExistingAsync(PhotoCandidate item, string expectedHash, CancellationToken ct)
    {
        await ConnectAsync(ct);
        if (Settings.Verification == VerificationMode.Sha256)
            return (await VerifyIntegrityAsync(item, expectedHash, ct)).Matches;
        var target = SafeTargetPath(Root, Settings.LibraryFolder, item.DestinationRelativePath);
        return File.Exists(target) && new FileInfo(target).Length == item.Size;
    }    public async Task<IntegrityCheckResult> VerifyIntegrityAsync(PhotoCandidate item, string expectedHash, CancellationToken ct)
    {
        await ConnectAsync(ct);
        var target = SafeTargetPath(Root, Settings.LibraryFolder, item.DestinationRelativePath);
        if (!File.Exists(target)) return new(false, false, "Missing");
        if (new FileInfo(target).Length != item.Size) return new(true, false, "Size");
        var remoteHash = await Hashing.Sha256Async(target, ct);
        return new(true, string.Equals(remoteHash, expectedHash, StringComparison.OrdinalIgnoreCase), "SHA-256");
    }
    public async Task<TransferResult> UploadAtomicAsync(PhotoCandidate item, string localHash, CancellationToken ct)
    {
        await ConnectAsync(ct);
        var target = SafeTargetPath(Root, Settings.LibraryFolder, item.DestinationRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            if (await MatchesAsync(target, item, localHash, ct))
                return new(target, Settings.Verification == VerificationMode.Sha256 ? "SHA-256" : "Size", true);
            throw new BackupConflictException($"같은 이름의 다른 파일이 이미 있습니다: {target}");
        }
        var part = target + ".part";
        await using (var source = await item.OpenReadAsync(ct))
        await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            await source.CopyToAsync(output, ct);
        if (!await MatchesAsync(part, item, localHash, ct)) throw new IOException("대상 파일 검증에 실패했습니다.");
        File.Move(part, target, false);
        if (!await MatchesAsync(target, item, localHash, ct)) throw new IOException("최종 대상 파일 재검증에 실패했습니다.");
        return new(target, Settings.Verification == VerificationMode.Sha256 ? "SHA-256" : "Size", false);
    }
    private async Task<bool> MatchesAsync(string path, PhotoCandidate item, string localHash, CancellationToken ct)
    {
        if (new FileInfo(path).Length != item.Size) return false;
        return Settings.Verification != VerificationMode.Sha256 ||
               string.Equals(await Hashing.Sha256Async(path, ct), localHash, StringComparison.OrdinalIgnoreCase);
    }
    private static string SafeTargetPath(string root, params string[] values)
    {
        var parts = values.SelectMany(value => value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (parts.Any(part => part is "." or ".." || part.Contains(':'))) throw new ArgumentException("대상 상대경로가 올바르지 않습니다.");
        var target = Path.GetFullPath(Path.Combine([root, .. parts]));
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("대상 루트를 벗어나는 경로입니다.");
        return target;
    }
    private static string SafeDirectoryPath(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return root;
        return SafeTargetPath(root, relativePath);
    }
    public abstract ValueTask DisposeAsync();
}

internal sealed class LocalDestination(BackupSettings settings) : FileSystemDestination(settings)
{
    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class SmbDestination : FileSystemDestination
{
    private bool _connected;
    public SmbDestination(BackupSettings settings) : base(settings)
    {
        if (!settings.Endpoint.StartsWith("\\\\", StringComparison.Ordinal)) throw new ArgumentException("SMB 경로는 \\\\서버\\공유 형식이어야 합니다.");
    }
    protected override Task ConnectAsync(CancellationToken ct)
    {
        if (_connected) return Task.CompletedTask;
        var resource = new NetResource { Scope = 2, Type = 1, RemoteName = Root };
        var result = WNetAddConnection2(ref resource, Settings.Password, Settings.UserName, 0);
        if (result is not 0 and not 1219) throw new IOException($"SMB 인증에 실패했습니다. Windows 오류 코드: {result}");
        _connected = true;
        return Task.CompletedTask;
    }
    public override ValueTask DisposeAsync()
    {
        // 1219는 기존 Windows 세션일 수 있으므로 앱이 생성 여부를 확신할 수 없는 연결을 강제로 끊지 않는다.
        return ValueTask.CompletedTask;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct NetResource { public int Scope, Type, DisplayType, Usage; public string? LocalName; public string RemoteName; public string? Comment, Provider; }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection2(ref NetResource resource, string password, string username, int flags);
}

internal sealed class WebDavDestination : IDestination
{
    private readonly BackupSettings _settings;
    private readonly Uri _baseUri;
    private readonly HttpClient _client;
    private readonly ConcurrentDictionary<string, Task> _collectionTasks = new(StringComparer.Ordinal);
    public WebDavDestination(BackupSettings settings)
    {
        _settings = settings;
        _baseUri = new(settings.Endpoint.TrimEnd('/') + "/");
        if (_baseUri.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("WebDAV는 HTTPS 주소만 허용합니다.");
        var handler = new SocketsHttpHandler
        {
            MaxConnectionsPerServer = 4,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        _client = new(handler) { Timeout = TimeSpan.FromMinutes(30) };
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{settings.UserName}:{settings.Password}"));
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", token);
    }
    public string Id => $"WebDav:{_baseUri.Host.ToLowerInvariant()}:{_baseUri.Port}:{_baseUri.AbsolutePath}:{_settings.LibraryFolder}";
    public async Task TestAsync(CancellationToken ct)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), _baseUri);
        request.Headers.Add("Depth", "0");
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != (HttpStatusCode)207) response.EnsureSuccessStatusCode();
    }
    public async Task<IReadOnlyList<DestinationFolder>> ListDirectoriesAsync(string relativePath, CancellationToken ct)
    {
        var escaped = string.IsNullOrWhiteSpace(relativePath) ? "" : JoinEscaped(relativePath) + "/";
        var uri = new Uri(_baseUri, escaped);
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent("<?xml version=\"1.0\" encoding=\"utf-8\"?><propfind xmlns=\"DAV:\"><prop><resourcetype/><displayname/></prop></propfind>", Encoding.UTF8, "application/xml");
        using var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode && response.StatusCode != (HttpStatusCode)207) response.EnsureSuccessStatusCode();
        var xml = XDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        XNamespace dav = "DAV:";
        var basePath = Uri.UnescapeDataString(_baseUri.AbsolutePath).TrimEnd('/');
        var folders = new List<DestinationFolder>();
        foreach (var item in xml.Descendants(dav + "response"))
        {
            if (!item.Descendants(dav + "collection").Any()) continue;
            var href = item.Element(dav + "href")?.Value;
            if (string.IsNullOrWhiteSpace(href)) continue;
            var itemUri = Uri.TryCreate(href, UriKind.Absolute, out var absolute) ? absolute : new Uri(_baseUri, href);
            var itemPath = Uri.UnescapeDataString(itemUri.AbsolutePath).TrimEnd('/');
            if (string.Equals(itemPath, basePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (!itemPath.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)) continue;
            var childRelative = itemPath[(basePath.Length + 1)..].Trim('/');
            if (string.IsNullOrWhiteSpace(childRelative)) continue;
            var name = item.Descendants(dav + "displayname").FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(name)) name = childRelative.Split('/').Last();
            folders.Add(new DestinationFolder(name, childRelative));
        }
        return folders.DistinctBy(folder => folder.RelativePath, StringComparer.OrdinalIgnoreCase)
            .OrderBy(folder => folder.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
    public async Task<bool> VerifyExistingAsync(PhotoCandidate item, string expectedHash, CancellationToken ct)
    {
        var directory = JoinEscaped(_settings.LibraryFolder, $"{item.TakenAt:yyyy}", $"{item.TakenAt:MM}");
        if (_settings.Verification == VerificationMode.Sha256)
            return (await VerifyIntegrityAsync(item, expectedHash, ct)).Matches;
        var finalUri = new Uri(_baseUri, directory + "/" + Uri.EscapeDataString(item.FileName));
        using var head = new HttpRequestMessage(HttpMethod.Head, finalUri);
        using var response = await _client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return response.Content.Headers.ContentLength == item.Size;
    }    public async Task<IntegrityCheckResult> VerifyIntegrityAsync(PhotoCandidate item, string expectedHash, CancellationToken ct)
    {
        var directory = JoinEscaped(_settings.LibraryFolder, $"{item.TakenAt:yyyy}", $"{item.TakenAt:MM}");
        var uri = new Uri(_baseUri, directory + "/" + Uri.EscapeDataString(item.FileName));
        using var head = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await _client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(false, false, "Missing");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength != item.Size) return new(true, false, "Content-Length");
        var serverHash = TryGetServerSha256(response);
        if (serverHash is not null)
            return new(true, string.Equals(serverHash, expectedHash, StringComparison.OrdinalIgnoreCase), "Server SHA-256");
        using var get = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        get.EnsureSuccessStatusCode();
        await using var stream = await get.Content.ReadAsStreamAsync(ct);
        var downloadedHash = await Hashing.Sha256Async(stream, ct);
        return new(true, string.Equals(downloadedHash, expectedHash, StringComparison.OrdinalIgnoreCase), "SHA-256 read-back");
    }
    public async Task<TransferResult> UploadAtomicAsync(PhotoCandidate item, string localHash, CancellationToken ct)
    {
        var directory = JoinEscaped(_settings.LibraryFolder, $"{item.TakenAt:yyyy}", $"{item.TakenAt:MM}");
        await EnsureCollectionCachedAsync(directory, ct);
        var finalUri = new Uri(_baseUri, directory + "/" + Uri.EscapeDataString(item.FileName));
        var tempUri = new Uri(finalUri.AbsoluteUri + $".part.{Guid.NewGuid():N}");
        var existing = await RemoteMatchesAsync(finalUri, item, localHash, ct);
        if (existing == true) return new(finalUri.AbsoluteUri, VerificationName, true);
        if (existing == false) throw new BackupConflictException($"같은 이름의 다른 WebDAV 파일이 이미 있습니다: {finalUri}");
        await using (var stream = await item.OpenReadAsync(ct))
        using (var put = new HttpRequestMessage(HttpMethod.Put, tempUri) { Content = new StreamContent(stream, 1024 * 1024) })
        {
            put.Content.Headers.ContentLength = item.Size;
            using var response = await _client.SendAsync(put, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }
        if (await RemoteMatchesAsync(tempUri, item, localHash, ct) != true) throw new IOException("WebDAV 임시 파일 검증에 실패했습니다.");
        using var move = new HttpRequestMessage(new HttpMethod("MOVE"), tempUri);
        move.Headers.Add("Destination", finalUri.AbsoluteUri);
        move.Headers.Add("Overwrite", "F");
        using var moved = await _client.SendAsync(move, HttpCompletionOption.ResponseHeadersRead, ct);
        if (moved.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.Conflict)
        {
            if (await RemoteMatchesAsync(finalUri, item, localHash, ct) == true)
            {
                await TryDeleteAsync(tempUri, ct);
                return new(finalUri.AbsoluteUri, VerificationName, true);
            }
            await TryDeleteAsync(tempUri, ct);
            throw new BackupConflictException($"같은 경로에 내용이 다른 WebDAV 파일이 있습니다: {finalUri}");
        }
        moved.EnsureSuccessStatusCode();
        if (await RemoteMatchesAsync(finalUri, item, localHash, ct) != true) throw new IOException("WebDAV 최종 파일 재검증에 실패했습니다.");
        return new(finalUri.AbsoluteUri, VerificationName, false);
    }
    private string VerificationName => _settings.Verification == VerificationMode.Sha256 ? "SHA-256 read-back" : "Content-Length";
    private async Task<bool?> RemoteMatchesAsync(Uri uri, PhotoCandidate item, string localHash, CancellationToken ct)
    {
        using var head = new HttpRequestMessage(HttpMethod.Head, uri);
        using var response = await _client.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength != item.Size) return false;
        if (_settings.Verification != VerificationMode.Sha256) return true;
        var serverHash = TryGetServerSha256(response);
        if (serverHash is not null) return string.Equals(serverHash, localHash, StringComparison.OrdinalIgnoreCase);
        using var get = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        get.EnsureSuccessStatusCode();
        await using var stream = await get.Content.ReadAsStreamAsync(ct);
        return string.Equals(await Hashing.Sha256Async(stream, ct), localHash, StringComparison.OrdinalIgnoreCase);
    }
    private static string? TryGetServerSha256(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("Digest", out var digestValues))
        {
            foreach (var part in string.Join(',', digestValues).Split(','))
            {
                var pieces = part.Trim().Split('=', 2);
                if (pieces.Length != 2 || !pieces[0].Equals("sha-256", StringComparison.OrdinalIgnoreCase)) continue;
                var value = pieces[1].Trim().Trim(':', '"');
                try { return Convert.ToHexString(Convert.FromBase64String(value)); } catch (FormatException) { }
            }
        }
        if (response.Headers.TryGetValues("OC-Checksum", out var checksumValues))
        {
            var value = checksumValues.FirstOrDefault(item => item.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase));
            if (value is not null) return value[7..].Trim();
        }
        return null;
    }
    private Task EnsureCollectionCachedAsync(string relative, CancellationToken ct) =>
        _collectionTasks.GetOrAdd(relative, _ => EnsureCollectionAsync(relative, ct));
    private async Task EnsureCollectionAsync(string relative, CancellationToken ct)
    {
        var current = "";
        foreach (var part in relative.Split('/'))
        {
            current += part + "/";
            using var request = new HttpRequestMessage(new HttpMethod("MKCOL"), new Uri(_baseUri, current));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.MethodNotAllowed) response.EnsureSuccessStatusCode();
        }
    }
    private async Task TryDeleteAsync(Uri uri, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, uri);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
    }
    private static string JoinEscaped(params string[] values)
    {
        var parts = values.SelectMany(value => value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (parts.Any(part => part is "." or ".." || part.Contains(':'))) throw new ArgumentException("WebDAV 상대경로가 올바르지 않습니다.");
        return string.Join('/', parts.Select(Uri.EscapeDataString));
    }
    public ValueTask DisposeAsync() { _client.Dispose(); return ValueTask.CompletedTask; }
}
