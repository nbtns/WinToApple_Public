import CryptoKit
import Foundation
import Security
import UIKit

struct LocalDeviceIdentity: Sendable {
    let deviceId: UUID
    let displayName: String
    let signingKey: P256.Signing.PrivateKey

    static func loadOrCreate() throws -> LocalDeviceIdentity {
        if let data = KeychainData.load(account: "device-identity") {
            do {
                let stored = try JSONDecoder().decode(StoredIdentity.self, from: data)
                guard let deviceId = UUID(uuidString: stored.deviceId),
                      let privateKey = Data(base64Encoded: stored.privateKey) else {
                    throw IdentityError.corruptIdentity
                }
                return LocalDeviceIdentity(
                    deviceId: deviceId,
                    displayName: stored.displayName,
                    signingKey: try P256.Signing.PrivateKey(rawRepresentation: privateKey)
                )
            } catch {
                KeychainData.delete(account: "device-identity")
                KeychainData.delete(account: "paired-windows")
            }
        }

        let key = P256.Signing.PrivateKey(compactRepresentable: false)
        let identity = LocalDeviceIdentity(
            deviceId: UUID(),
            displayName: sanitizeDeviceName(UIDevice.current.name),
            signingKey: key
        )
        let stored = StoredIdentity(
            deviceId: identity.deviceId.uuidString.lowercased(),
            displayName: identity.displayName,
            privateKey: key.rawRepresentation.base64EncodedString()
        )
        try KeychainData.save(try JSONEncoder().encode(stored), account: "device-identity")
        return identity
    }

    private static func sanitizeDeviceName(_ value: String) -> String {
        let cleaned = value.replacingOccurrences(of: "\n", with: " ").replacingOccurrences(of: "\r", with: " ").trimmingCharacters(in: .whitespaces)
        return String((cleaned.isEmpty ? "iPhone" : cleaned).prefix(64))
    }
}

private enum IdentityError: Error {
    case corruptIdentity
}

struct PairedWindowsDevice: Codable, Sendable, Hashable {
    let deviceId: String
    let displayName: String
    let publicKey: String
    let pairedAt: Date
}

final class PairedDeviceStore: @unchecked Sendable {
    private let lock = NSLock()

    func all() -> [PairedWindowsDevice] {
        lock.lock()
        defer { lock.unlock() }
        guard let data = KeychainData.load(account: "paired-windows") else { return [] }
        return (try? JSONDecoder().decode([PairedWindowsDevice].self, from: data)) ?? []
    }

    func find(deviceId: UUID) -> PairedWindowsDevice? {
        all().first { $0.deviceId.caseInsensitiveCompare(deviceId.uuidString) == .orderedSame }
    }

    func save(_ device: PairedWindowsDevice) throws {
        lock.lock()
        defer { lock.unlock() }
        let devices = [device]
        try KeychainData.save(try JSONEncoder().encode(devices), account: "paired-windows")
    }

    func removeAll() {
        lock.lock()
        defer { lock.unlock() }
        KeychainData.delete(account: "paired-windows")
    }
}

final class PairingSecret: @unchecked Sendable {
    private let lock = NSLock()
    private var secret: Data

    init() throws {
        secret = try Phase1Crypto.randomData(count: 16)
    }

    func currentData() -> Data {
        lock.lock()
        defer { lock.unlock() }
        return secret
    }

    func displayCode() -> String {
        let plain = Base32Codec.encode(currentData())
        return stride(from: 0, to: plain.count, by: 4).map { start in
            let lower = plain.index(plain.startIndex, offsetBy: start)
            let upper = plain.index(lower, offsetBy: min(4, plain.count - start))
            return String(plain[lower..<upper])
        }.joined(separator: "-")
    }

    func rotate() throws {
        let replacement = try Phase1Crypto.randomData(count: 16)
        lock.lock()
        secret = replacement
        lock.unlock()
    }
}

enum Base32Codec {
    private static let alphabet = Array("ABCDEFGHIJKLMNOPQRSTUVWXYZ234567")

    static func encode(_ data: Data) -> String {
        var output = ""
        var buffer = 0
        var bits = 0
        for byte in data {
            buffer = (buffer << 8) | Int(byte)
            bits += 8
            while bits >= 5 {
                bits -= 5
                output.append(alphabet[(buffer >> bits) & 31])
                buffer &= (1 << bits) - 1
            }
        }
        if bits > 0 { output.append(alphabet[(buffer << (5 - bits)) & 31]) }
        return output
    }
}

private struct StoredIdentity: Codable {
    let deviceId: String
    let displayName: String
    let privateKey: String
}

enum KeychainData {
    private static let service = "app.localbridge.identity"

    static func load(account: String) -> Data? {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
            kSecReturnData: true,
            kSecMatchLimit: kSecMatchLimitOne,
        ]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess else { return nil }
        return result as? Data
    }

    static func save(_ data: Data, account: String) throws {
        delete(account: account)
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
            kSecAttrAccessible: kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly,
            kSecValueData: data,
        ]
        let status = SecItemAdd(query as CFDictionary, nil)
        guard status == errSecSuccess else { throw KeychainError.status(status) }
    }

    static func delete(account: String) {
        let query: [CFString: Any] = [
            kSecClass: kSecClassGenericPassword,
            kSecAttrService: service,
            kSecAttrAccount: account,
        ]
        SecItemDelete(query as CFDictionary)
    }
}

enum KeychainError: LocalizedError {
    case status(OSStatus)
    var errorDescription: String? {
        switch self {
        case .status(let status):
            "端末内の安全な保存領域を利用できません。（OSStatus: \(status)）"
        }
    }
}
