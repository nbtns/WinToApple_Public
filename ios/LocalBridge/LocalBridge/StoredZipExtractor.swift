import Foundation

enum StoredZipExtractor {
    private static let localHeader: UInt32 = 0x04034b50
    private static let centralHeader: UInt32 = 0x02014b50
    private static let endOfCentralDirectory: UInt32 = 0x06054b50
    private static let maximumEntries = 100_000
    private static let maximumExpandedSize: UInt64 = 20 * 1024 * 1024 * 1024

    static func extract(archiveURL: URL, destinationDirectory: URL) throws -> URL {
        let temporaryRoot = destinationDirectory.appendingPathComponent(".localbridge-extract-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: temporaryRoot, withIntermediateDirectories: false)
        var succeeded = false
        defer { if !succeeded { try? FileManager.default.removeItem(at: temporaryRoot) } }

        let archive = try FileHandle(forReadingFrom: archiveURL)
        defer { try? archive.close() }
        var entryCount = 0
        var totalSize: UInt64 = 0

        while true {
            let signatureData = try readUpTo(archive, count: 4)
            if signatureData.isEmpty { break }
            guard signatureData.count == 4 else { throw ArchiveError.truncated }
            let signature = signatureData.readUInt32Little(at: 0)
            if signature == centralHeader || signature == endOfCentralDirectory { break }
            guard signature == localHeader else { throw ArchiveError.invalidHeader }

            let header = try readExactly(archive, count: 26)
            let flags = header.readUInt16Little(at: 2)
            let method = header.readUInt16Little(at: 4)
            let expectedCrc = header.readUInt32Little(at: 10)
            let compressedSize = header.readUInt32Little(at: 14)
            let expandedSize = header.readUInt32Little(at: 18)
            let nameLength = Int(header.readUInt16Little(at: 22))
            let extraLength = Int(header.readUInt16Little(at: 24))

            guard flags & 0x1 == 0 else { throw ArchiveError.encryptedEntry }
            guard flags & 0x8 == 0 else { throw ArchiveError.dataDescriptorUnsupported }
            guard method == 0 else { throw ArchiveError.compressedEntryUnsupported }
            guard compressedSize == expandedSize, compressedSize != UInt32.max else { throw ArchiveError.archiveTooLarge }
            guard nameLength > 0 && nameLength <= 4096 else { throw ArchiveError.invalidPath }
            let nameData = try readExactly(archive, count: nameLength)
            guard let rawName = String(data: nameData, encoding: .utf8) else { throw ArchiveError.invalidPath }
            if extraLength > 0 { _ = try readExactly(archive, count: extraLength) }

            let components = try safeComponents(rawName)
            let destination = components.reduce(temporaryRoot) { $0.appendingPathComponent($1) }
            entryCount += 1
            guard entryCount <= maximumEntries else { throw ArchiveError.tooManyEntries }
            totalSize += UInt64(expandedSize)
            guard totalSize <= maximumExpandedSize else { throw ArchiveError.archiveTooLarge }

            if rawName.hasSuffix("/") || rawName.hasSuffix("\\") {
                try FileManager.default.createDirectory(at: destination, withIntermediateDirectories: true)
                continue
            }

            try FileManager.default.createDirectory(at: destination.deletingLastPathComponent(), withIntermediateDirectories: true)
            guard FileManager.default.createFile(atPath: destination.path, contents: nil) else { throw CocoaError(.fileWriteUnknown) }
            let output = try FileHandle(forWritingTo: destination)
            var remaining = UInt64(compressedSize)
            var crc = Crc32.initial
            do {
                while remaining > 0 {
                    let count = Int(min(remaining, 1024 * 1024))
                    let chunk = try readExactly(archive, count: count)
                    try output.write(contentsOf: chunk)
                    crc = Crc32.update(crc, with: chunk)
                    remaining -= UInt64(count)
                }
                try output.close()
            } catch {
                try? output.close()
                throw error
            }
            guard Crc32.finish(crc) == expectedCrc else {
                try? FileManager.default.removeItem(at: destination)
                throw ArchiveError.crcMismatch
            }
        }

        let children = try FileManager.default.contentsOfDirectory(at: temporaryRoot, includingPropertiesForKeys: nil)
        guard !children.isEmpty else { throw ArchiveError.emptyArchive }
        let source: URL
        if children.count == 1 {
            source = children[0]
        } else {
            let name = archiveURL.lastPathComponent.replacingOccurrences(of: ".localbridge-folder.zip", with: "")
            let container = temporaryRoot.appendingPathComponent(name.isEmpty ? "受信フォルダ" : name, isDirectory: true)
            try FileManager.default.createDirectory(at: container, withIntermediateDirectories: false)
            for child in children { try FileManager.default.moveItem(at: child, to: container.appendingPathComponent(child.lastPathComponent)) }
            source = container
        }
        let finalURL = DestinationRouter.uniqueDestination(in: destinationDirectory, requestedName: source.lastPathComponent)
        try FileManager.default.moveItem(at: source, to: finalURL)
        try FileManager.default.removeItem(at: temporaryRoot)
        succeeded = true
        return finalURL
    }

    private static func safeComponents(_ path: String) throws -> [String] {
        guard !path.hasPrefix("/"), !path.hasPrefix("\\"), !path.contains("\0") else { throw ArchiveError.invalidPath }
        let normalized = path.replacingOccurrences(of: "\\", with: "/")
        let components = normalized.split(separator: "/", omittingEmptySubsequences: true).map(String.init)
        guard !components.isEmpty,
              components.allSatisfy({ !$0.isEmpty && $0 != "." && $0 != ".." && !$0.contains(":") }) else {
            throw ArchiveError.invalidPath
        }
        return components
    }

    private static func readExactly(_ handle: FileHandle, count: Int) throws -> Data {
        let data = try readUpTo(handle, count: count)
        guard data.count == count else { throw ArchiveError.truncated }
        return data
    }

    private static func readUpTo(_ handle: FileHandle, count: Int) throws -> Data {
        try handle.read(upToCount: count) ?? Data()
    }
}

enum ArchiveError: LocalizedError {
    case truncated, invalidHeader, encryptedEntry, dataDescriptorUnsupported, compressedEntryUnsupported
    case archiveTooLarge, invalidPath, tooManyEntries, crcMismatch, emptyArchive

    var errorDescription: String? {
        switch self {
        case .truncated: "フォルダデータが途中で切れています。"
        case .invalidHeader: "フォルダデータの形式が不正です。"
        case .encryptedEntry: "暗号化ZIPは展開できません。"
        case .dataDescriptorUnsupported, .compressedEntryUnsupported: "このフォルダデータの圧縮方式には対応していません。"
        case .archiveTooLarge: "展開後のフォルダが上限を超えています。"
        case .invalidPath: "安全でないフォルダ内パスを拒否しました。"
        case .tooManyEntries: "フォルダ内の項目数が上限を超えています。"
        case .crcMismatch: "フォルダ内ファイルの破損を検出しました。"
        case .emptyArchive: "受信したフォルダが空です。"
        }
    }
}

private enum Crc32 {
    static let initial: UInt32 = 0xffffffff
    private static let table: [UInt32] = (0..<256).map { value in
        var crc = UInt32(value)
        for _ in 0..<8 { crc = (crc & 1) == 1 ? 0xedb88320 ^ (crc >> 1) : crc >> 1 }
        return crc
    }

    static func update(_ state: UInt32, with data: Data) -> UInt32 {
        var crc = state
        for byte in data { crc = table[Int((crc ^ UInt32(byte)) & 0xff)] ^ (crc >> 8) }
        return crc
    }

    static func finish(_ state: UInt32) -> UInt32 { state ^ 0xffffffff }
}

private extension Data {
    func readUInt16Little(at offset: Int) -> UInt16 {
        UInt16(self[offset]) | (UInt16(self[offset + 1]) << 8)
    }

    func readUInt32Little(at offset: Int) -> UInt32 {
        UInt32(self[offset])
            | (UInt32(self[offset + 1]) << 8)
            | (UInt32(self[offset + 2]) << 16)
            | (UInt32(self[offset + 3]) << 24)
    }
}

