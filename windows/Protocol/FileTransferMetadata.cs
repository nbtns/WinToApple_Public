namespace LocalBridge.Protocol;

public sealed record FileTransferMetadata(
    int ProtocolVersion,
    string FileName,
    long Size,
    DateTimeOffset ModifiedUtc,
    string MimeType,
    string Sha256);

public sealed record TransferResult(bool Success, string? SavedName, string? Error);

