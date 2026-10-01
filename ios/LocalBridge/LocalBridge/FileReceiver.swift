import CryptoKit
import Foundation
import Network

struct ReceivedFile: Sendable {
    let savedName: String
    let size: Int64
}

enum TransferError: LocalizedError {
    case connectionClosed
    case unsupportedProtocol
    case invalidMessage
    case invalidFileName
    case recordTooLarge
    case fileTooLarge
    case insufficientStorage
    case offsetMismatch
    case sizeMismatch
    case hashMismatch
    case randomGenerationFailed
    case unpairedDevice
    case authenticationFailed

    var errorDescription: String? {
        switch self {
        case .connectionClosed: "接続が途中で閉じられました。次回は続きから再開します。"
        case .unsupportedProtocol: "対応していない通信方式です。"
        case .invalidMessage: "受信データの形式が不正です。"
        case .invalidFileName: "安全でないファイル名を拒否しました。"
        case .recordTooLarge: "受信データが上限を超えています。"
        case .fileTooLarge: "ファイルが上限20 GiBを超えています。"
        case .insufficientStorage: "iPhoneの空き容量が不足しています。"
        case .offsetMismatch: "ファイルの受信順序が不正です。"
        case .sizeMismatch: "受信したファイルサイズが一致しません。"
        case .hashMismatch: "破損を検出したため、ファイルを保存しませんでした。"
        case .randomGenerationFailed: "安全な乱数を生成できませんでした。"
        case .unpairedDevice: "登録されていないWindowsからの接続を拒否しました。"
        case .authenticationFailed: "Windowsの本人確認に失敗しました。"
        }
    }
}

enum FileReceiver {
    private static let maxFileLength: Int64 = 20 * 1024 * 1024 * 1024
    private static let maxMetadataLength = 64 * 1024

    static func receive(
        connection: NWConnection,
        identity: LocalDeviceIdentity,
        pairedDevices: PairedDeviceStore,
        router: DestinationRouter,
        onMetadata: @Sendable (String) -> Void
    ) async throws -> ReceivedFile {
        let buffered = BufferedConnection(connection)
        try await buffered.start()
        let channel = try await AuthenticatedCrypto.accept(on: buffered, identity: identity, pairedDevices: pairedDevices)

        do {
            let result = try await receiveFile(channel: channel, router: router, onMetadata: onMetadata)
            try await sendResult(channel: channel, success: true, savedName: result.savedName, error: nil)
            return result
        } catch {
            try? await sendResult(channel: channel, success: false, savedName: nil, error: error.localizedDescription)
            throw error
        }
    }

    private static func receiveFile(
        channel: SecureChannel,
        router: DestinationRouter,
        onMetadata: @Sendable (String) -> Void
    ) async throws -> ReceivedFile {
        let metadataMessage = try await channel.readRecord()
        guard metadataMessage.count > 1,
              metadataMessage[0] == 0x01,
              metadataMessage.count - 1 <= maxMetadataLength else {
            throw TransferError.invalidMessage
        }

        let metadata = try JSONDecoder().decode(TransferMetadata.self, from: metadataMessage.dropFirst())
        guard metadata.protocolVersion == 2,
              metadata.size >= 0,
              metadata.size <= maxFileLength,
              metadata.fileId.count == 64,
              metadata.fileId.allSatisfy({ $0.isHexDigit }) else {
            throw TransferError.fileTooLarge
        }
        let safeName = try sanitizedFileName(metadata.fileName)
        try ensureStorage(for: metadata.size)
        onMetadata(safeName)

        let partialDirectory = try partialFilesDirectory()
        let partialURL = partialDirectory.appendingPathComponent(metadata.fileId.lowercased() + ".partial")
        if FileManager.default.fileExists(atPath: partialURL.path) {
            let size = try partialURL.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0
            if Int64(size) > metadata.size { try FileManager.default.removeItem(at: partialURL) }
        }
        if !FileManager.default.fileExists(atPath: partialURL.path) {
            guard FileManager.default.createFile(atPath: partialURL.path, contents: nil) else { throw CocoaError(.fileWriteUnknown) }
        }

        let currentSize = Int64((try partialURL.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
        var resume = Data([0x11])
        resume.append(Data(uint64: UInt64(currentSize)))
        try await channel.writeRecord(resume)

        let handle = try FileHandle(forWritingTo: partialURL)
        try handle.seekToEnd()
        var hasher = try hashExistingFile(partialURL)
        var received = currentSize
        var closed = false
        defer { if !closed { try? handle.close() } }

        while true {
            let message = try await channel.readRecord()
            guard let type = message.first else { throw TransferError.invalidMessage }
            switch type {
            case 0x02:
                guard message.count >= 9 else { throw TransferError.invalidMessage }
                let offset = message.readUInt64(at: 1)
                guard offset == UInt64(received) else { throw TransferError.offsetMismatch }
                let chunk = message.dropFirst(9)
                guard received + Int64(chunk.count) <= metadata.size else { throw TransferError.sizeMismatch }
                try handle.write(contentsOf: chunk)
                hasher.update(data: chunk)
                received += Int64(chunk.count)
            case 0x03:
                guard message.count == 1 else { throw TransferError.invalidMessage }
                guard received == metadata.size else { throw TransferError.sizeMismatch }
                try handle.synchronize()
                try handle.close()
                closed = true
                let actualHash = Data(hasher.finalize()).map { String(format: "%02x", $0) }.joined()
                guard actualHash == metadata.sha256.lowercased() else {
                    try? FileManager.default.removeItem(at: partialURL)
                    throw TransferError.hashMismatch
                }
                let inbox = try inboxDirectory()
                let inboxURL = DestinationRouter.uniqueDestination(in: inbox, requestedName: safeName)
                try FileManager.default.moveItem(at: partialURL, to: inboxURL)
                let routedName = try await router.route(
                    fileURL: inboxURL,
                    originalName: safeName,
                    mimeType: metadata.mimeType,
                    folderArchive: metadata.folderArchive
                )
                return ReceivedFile(savedName: routedName, size: received)
            default:
                throw TransferError.invalidMessage
            }
        }
    }

    private static func hashExistingFile(_ url: URL) throws -> SHA256 {
        var hasher = SHA256()
        let reader = try FileHandle(forReadingFrom: url)
        defer { try? reader.close() }
        while let data = try reader.read(upToCount: 1024 * 1024), !data.isEmpty { hasher.update(data: data) }
        return hasher
    }

    private static func sanitizedFileName(_ input: String) throws -> String {
        let trimmed = input.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty,
              trimmed != ".",
              trimmed != "..",
              !trimmed.contains("\0"),
              URL(fileURLWithPath: trimmed).lastPathComponent == trimmed else {
            throw TransferError.invalidFileName
        }
        return trimmed
    }

    private static func documentsDirectory() throws -> URL {
        try FileManager.default.url(for: .documentDirectory, in: .userDomainMask, appropriateFor: nil, create: true)
    }

    private static func inboxDirectory() throws -> URL {
        let inbox = try documentsDirectory().appendingPathComponent("Inbox", isDirectory: true)
        try FileManager.default.createDirectory(at: inbox, withIntermediateDirectories: true)
        return inbox
    }

    private static func partialFilesDirectory() throws -> URL {
        let partial = try documentsDirectory().appendingPathComponent(".Partial", isDirectory: true)
        try FileManager.default.createDirectory(at: partial, withIntermediateDirectories: true)
        return partial
    }

    private static func ensureStorage(for size: Int64) throws {
        let home = URL(fileURLWithPath: NSHomeDirectory())
        let values = try home.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey])
        if let available = values.volumeAvailableCapacityForImportantUsage,
           available < size + 16 * 1024 * 1024 {
            throw TransferError.insufficientStorage
        }
    }

    private static func sendResult(channel: SecureChannel, success: Bool, savedName: String?, error: String?) async throws {
        let result = TransferResultPayload(success: success, savedName: savedName, error: error)
        var message = Data([0x10, success ? 0x00 : 0x01])
        message.append(try JSONEncoder().encode(result))
        try await channel.writeRecord(message)
    }
}

private struct TransferMetadata: Decodable {
    let protocolVersion: Int
    let fileId: String
    let fileName: String
    let size: Int64
    let modifiedUtc: String
    let mimeType: String
    let sha256: String
    let folderArchive: Bool
}

private struct TransferResultPayload: Encodable {
    let success: Bool
    let savedName: String?
    let error: String?
}

extension Data {
    init(uint32 value: UInt32) {
        self = Data([
            UInt8((value >> 24) & 0xff),
            UInt8((value >> 16) & 0xff),
            UInt8((value >> 8) & 0xff),
            UInt8(value & 0xff),
        ])
    }

    init(uint64 value: UInt64) {
        self = Data((0..<8).map { shift in UInt8((value >> UInt64((7 - shift) * 8)) & 0xff) })
    }

    func readUInt32(at offset: Int) -> UInt32 {
        (UInt32(self[offset]) << 24)
            | (UInt32(self[offset + 1]) << 16)
            | (UInt32(self[offset + 2]) << 8)
            | UInt32(self[offset + 3])
    }

    func readUInt64(at offset: Int) -> UInt64 {
        var value: UInt64 = 0
        for index in 0..<8 { value = (value << 8) | UInt64(self[offset + index]) }
        return value
    }
}
