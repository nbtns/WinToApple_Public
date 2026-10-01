namespace LocalBridge.Protocol;

public static class ProtocolConstants
{
    public static ReadOnlySpan<byte> Magic => "LBP1"u8;
    public static ReadOnlySpan<byte> HkdfInfo => "LocalBridge LBP1 AES-256-GCM"u8;
    public const int PublicKeyLength = 65;
    public const int HandshakeNonceLength = 32;
    public const int GcmNonceLength = 12;
    public const int GcmTagLength = 16;
    public const int ChunkSize = 4 * 1024 * 1024;
    public const int MaxRecordLength = 5 * 1024 * 1024;
    public const long MaxFileLength = 20L * 1024 * 1024 * 1024;
    public const int MaxMetadataLength = 64 * 1024;
}

public enum MessageType : byte
{
    Metadata = 0x01,
    Chunk = 0x02,
    Complete = 0x03,
    Result = 0x10,
    Resume = 0x11,
}
