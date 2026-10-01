import CryptoKit
import Foundation
import Security

enum Phase1Crypto {
    static let magic = Data("LBP1".utf8)
    static let publicKeyLength = 65
    static let nonceLength = 32
    static let info = Data("LocalBridge LBP1 AES-256-GCM".utf8)

    static func accept(on connection: BufferedConnection) async throws -> SecureChannel {
        let privateKey = P256.KeyAgreement.PrivateKey()
        let serverNonce = try randomData(count: nonceLength)
        var hello = magic
        hello.append(privateKey.publicKey.x963Representation)
        hello.append(serverNonce)
        try await connection.send(hello)

        let clientHello = try await connection.readExactly(magic.count + publicKeyLength + nonceLength)
        guard clientHello.prefix(magic.count) == magic else {
            throw TransferError.unsupportedProtocol
        }

        let publicStart = magic.count
        let nonceStart = publicStart + publicKeyLength
        let clientPublicData = Data(clientHello[publicStart..<nonceStart])
        let clientNonce = Data(clientHello[nonceStart..<(nonceStart + nonceLength)])
        let clientPublic = try P256.KeyAgreement.PublicKey(x963Representation: clientPublicData)
        let sharedSecret = try privateKey.sharedSecretFromKeyAgreement(with: clientPublic)
        let key = sharedSecret.hkdfDerivedSymmetricKey(
            using: SHA256.self,
            salt: serverNonce + clientNonce,
            sharedInfo: info,
            outputByteCount: 32
        )
        return SecureChannel(connection: connection, key: key)
    }

    static func randomData(count: Int) throws -> Data {
        var data = Data(count: count)
        let status = data.withUnsafeMutableBytes { buffer in
            SecRandomCopyBytes(kSecRandomDefault, count, buffer.baseAddress!)
        }
        guard status == errSecSuccess else { throw TransferError.randomGenerationFailed }
        return data
    }
}

final class SecureChannel: @unchecked Sendable {
    private static let maxRecordLength = 5 * 1024 * 1024
    private let connection: BufferedConnection
    private let key: SymmetricKey

    init(connection: BufferedConnection, key: SymmetricKey) {
        self.connection = connection
        self.key = key
    }

    func readRecord() async throws -> Data {
        let lengthData = try await connection.readExactly(4)
        let length = Int(lengthData.readUInt32(at: 0))
        guard length <= Self.maxRecordLength else { throw TransferError.recordTooLarge }

        let nonceData = try await connection.readExactly(12)
        let ciphertext = try await connection.readExactly(length)
        let tag = try await connection.readExactly(16)
        let nonce = try AES.GCM.Nonce(data: nonceData)
        let sealed = try AES.GCM.SealedBox(nonce: nonce, ciphertext: ciphertext, tag: tag)
        return try AES.GCM.open(sealed, using: key)
    }

    func writeRecord(_ plaintext: Data) async throws {
        guard plaintext.count <= Self.maxRecordLength else { throw TransferError.recordTooLarge }
        let nonceData = try Phase1Crypto.randomData(count: 12)
        let nonce = try AES.GCM.Nonce(data: nonceData)
        let sealed = try AES.GCM.seal(plaintext, using: key, nonce: nonce)
        var frame = Data(uint32: UInt32(plaintext.count))
        frame.append(nonceData)
        frame.append(sealed.ciphertext)
        frame.append(sealed.tag)
        try await connection.send(frame)
    }
}

