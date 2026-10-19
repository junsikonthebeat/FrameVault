namespace FrameVault;

// 기존 DPAPI 설정은 enum을 숫자로 저장하므로 Smb/WebDav 값은 바꾸지 않는다.
public enum BackupProtocol { Smb = 0, WebDav = 1, Local = 2 }
public enum VerificationMode { Size, Sha256 }
public enum TransferState { Discovered, Staging, Staged, Uploading, RemoteVerified, Completed, Failed }

public sealed record BackupSettings(
    string SourceFolder,
    BackupProtocol Protocol,
    string Endpoint,
    string LibraryFolder,
    string UserName,
    string Password,
    VerificationMode Verification = VerificationMode.Size,
    string? CacheFolder = null,
    bool AutoDetect = true,
    int CacheLimitGb = 50,
    DateTimeOffset? TakenFrom = null,
    DateTimeOffset? TakenTo = null);

public sealed record BackupProfile(string Name, BackupSettings Settings);
public sealed record ProtectedSettingsDocument(
    int Version,
    List<BackupProfile> Profiles,
    int ActiveProfileIndex = 0,
    string? CacheFolder = null,
    bool AutoDetect = true,
    int CacheLimitGb = 50,
    string Theme = "Light");

public sealed record PhotoCandidate(
    string FullPath,
    string RelativePath,
    long Size,
    DateTime LastWriteUtc,
    DateTime TakenAt,
    string SourceId = "folder",
    bool IsStaged = false,
    Func<CancellationToken, Task<Stream>>? OpenSourceStream = null)
{
    public Task<Stream> OpenReadAsync(CancellationToken ct) => OpenSourceStream is null
        ? Task.FromResult<Stream>(new FileStream(FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        : OpenSourceStream(ct);
    public string FileName => Path.GetFileName(FullPath);
    public string DestinationRelativePath => $"{TakenAt:yyyy}/{TakenAt:MM}/{FileName}";
    public string SourceKey => $"{SourceId}|{RelativePath}|{Size}|{LastWriteUtc.Ticks}";
}

public sealed record TransferResult(string DestinationPath, string Verification, bool AlreadyExisted);
public sealed record IntegrityCheckResult(bool Exists, bool Matches, string Method);

public sealed record DestinationFolder(string Name, string RelativePath)
{
    public override string ToString() => Name;
}

public sealed class BackupConflictException(string message) : IOException(message);

