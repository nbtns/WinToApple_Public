import Foundation
import Photos
import UniformTypeIdentifiers

enum DestinationPreferences {
    private static let bookmarkKey = "LocalBridge.destinationBookmark"
    private static let nameKey = "LocalBridge.destinationName"

    static var folderName: String? { UserDefaults.standard.string(forKey: nameKey) }

    static func saveFolder(_ url: URL) throws {
        let accessed = url.startAccessingSecurityScopedResource()
        defer { if accessed { url.stopAccessingSecurityScopedResource() } }
        let bookmark = try url.bookmarkData(options: .minimalBookmark, includingResourceValuesForKeys: nil, relativeTo: nil)
        UserDefaults.standard.set(bookmark, forKey: bookmarkKey)
        UserDefaults.standard.set(url.lastPathComponent, forKey: nameKey)
    }

    static func clearFolder() {
        UserDefaults.standard.removeObject(forKey: bookmarkKey)
        UserDefaults.standard.removeObject(forKey: nameKey)
    }

    static func resolvedFolder() -> URL? {
        guard let bookmark = UserDefaults.standard.data(forKey: bookmarkKey) else { return nil }
        var stale = false
        guard let url = try? URL(
            resolvingBookmarkData: bookmark,
            options: .withoutUI,
            relativeTo: nil,
            bookmarkDataIsStale: &stale
        ), !stale else {
            clearFolder()
            return nil
        }
        return url
    }
}

final class DestinationRouter: @unchecked Sendable {
    func route(fileURL: URL, originalName: String, mimeType: String, folderArchive: Bool) async throws -> String {
        if folderArchive {
            let destination = DestinationPreferences.resolvedFolder() ?? fileURL.deletingLastPathComponent()
            let accessed = destination.startAccessingSecurityScopedResource()
            defer { if accessed { destination.stopAccessingSecurityScopedResource() } }
            let extracted = try StoredZipExtractor.extract(archiveURL: fileURL, destinationDirectory: destination)
            try FileManager.default.removeItem(at: fileURL)
            return extracted.lastPathComponent
        }

        if Self.isPhotoOrVideo(name: originalName, mimeType: mimeType), await saveToPhotos(fileURL: fileURL, mimeType: mimeType) {
            try FileManager.default.removeItem(at: fileURL)
            return "写真/\(originalName)"
        }

        guard let destination = DestinationPreferences.resolvedFolder() else {
            return originalName
        }
        let accessed = destination.startAccessingSecurityScopedResource()
        defer { if accessed { destination.stopAccessingSecurityScopedResource() } }
        let finalURL = uniqueDestination(in: destination, requestedName: originalName)
        try FileManager.default.copyItem(at: fileURL, to: finalURL)
        try FileManager.default.removeItem(at: fileURL)
        return finalURL.lastPathComponent
    }

    private func saveToPhotos(fileURL: URL, mimeType: String) async -> Bool {
        let status = await PHPhotoLibrary.requestAuthorization(for: .addOnly)
        guard status == .authorized || status == .limited else { return false }
        return await withCheckedContinuation { continuation in
            PHPhotoLibrary.shared().performChanges {
                if mimeType.hasPrefix("video/") {
                    PHAssetChangeRequest.creationRequestForAssetFromVideo(atFileURL: fileURL)
                } else {
                    PHAssetChangeRequest.creationRequestForAssetFromImage(atFileURL: fileURL)
                }
            } completionHandler: { success, _ in
                continuation.resume(returning: success)
            }
        }
    }

    static func uniqueDestination(in directory: URL, requestedName: String) -> URL {
        let original = directory.appendingPathComponent(requestedName)
        guard FileManager.default.fileExists(atPath: original.path) else { return original }
        let extensionName = original.pathExtension
        let stem = original.deletingPathExtension().lastPathComponent
        var number = 1
        while true {
            let name = extensionName.isEmpty ? "\(stem) (\(number))" : "\(stem) (\(number)).\(extensionName)"
            let candidate = directory.appendingPathComponent(name)
            if !FileManager.default.fileExists(atPath: candidate.path) { return candidate }
            number += 1
        }
    }

    private static func isPhotoOrVideo(name: String, mimeType: String) -> Bool {
        if mimeType.hasPrefix("image/") || mimeType.hasPrefix("video/") { return true }
        let ext = URL(fileURLWithPath: name).pathExtension.lowercased()
        return ["jpg", "jpeg", "png", "heic", "gif", "mp4", "mov"].contains(ext)
    }
}

