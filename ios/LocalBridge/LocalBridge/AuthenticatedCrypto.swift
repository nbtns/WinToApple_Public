import CryptoKit
import Foundation

enum AuthenticatedCrypto {
    private static let magic = Data("LBP2".utf8)
    private static let serverContext = Data("LocalBridge-LBP2-SERVER".utf8)
    private static let hkdfContext = Data("LocalBridge LBP2 AES-256-GCM".utf8)
    private static let fixedClientLength = 4 + 16 + 65 + 32
    private static let maxSignatureLength = 256

    static func accept(
        on connection: BufferedConnection,
        identity: LocalDeviceIdentity,
        pairedDevices: PairedDeviceStore
    ) async throws -> SecureChannel {
        let ephemeral = P256.KeyAgreement.PrivateKey(compactRepresentable: false)
        let serverNonce = try Phase1Crypto.randomData(count: 32)
        var serverFixed = magic
        serverFixed.append(identity.deviceId.networkData)
        serverFixed.append(identity.signingKey.publicKey.x963Representation)
        serverFixed.append(ephemeral.publicKey.x963Representation)
        serverFixed.append(serverNonce)

        var signedServer = serverContext
        signedServer.append(serverFixed)
        let serverSignature = try identity.signingKey.signature(for: signedServer).derRepresentation
        guard serverSignature.count <= maxSignatureLength else { throw TransferError.invalidMessage }
        var serverHello = serverFixed
        serverHello.append(Data(uint16: UInt16(serverSignature.count)))
        serverHello.append(serverSignature)
        try await connection.send(serverHello)

        let clientFixed = try await connection.readExactly(fixedClientLength)
        guard clientFixed.prefix(magic.count) == magic else { throw TransferError.unsupportedProtocol }
        let clientIdData = Data(clientFixed[4..<20])
        guard let clientId = UUID(networkData: clientIdData),
              let paired = pairedDevices.find(deviceId: clientId),
              let pairedPublicData = Data(base64Encoded: paired.publicKey) else {
            throw TransferError.unpairedDevice
        }

        let signatureLength = Int((try await connection.readExactly(2)).readUInt16(at: 0))
        guard signatureLength > 0 && signatureLength <= maxSignatureLength else { throw TransferError.invalidMessage }
        let clientSignatureData = try await connection.readExactly(signatureLength)
        var clientSigned = serverHello
        clientSigned.append(clientFixed)
        let clientPublic = try P256.Signing.PublicKey(x963Representation: pairedPublicData)
        let clientSignature = try P256.Signing.ECDSASignature(derRepresentation: clientSignatureData)
        guard clientPublic.isValidSignature(clientSignature, for: clientSigned) else {
            throw TransferError.authenticationFailed
        }

        let clientEphemeralData = Data(clientFixed[20..<85])
        let clientNonce = Data(clientFixed[85..<117])
        let clientEphemeral = try P256.KeyAgreement.PublicKey(x963Representation: clientEphemeralData)
        let sharedSecret = try ephemeral.sharedSecretFromKeyAgreement(with: clientEphemeral)
        var salt = serverNonce
        salt.append(clientNonce)
        var info = hkdfContext
        info.append(Data(SHA256.hash(data: clientSigned)))
        let key = sharedSecret.hkdfDerivedSymmetricKey(
            using: SHA256.self,
            salt: salt,
            sharedInfo: info,
            outputByteCount: 32
        )
        return SecureChannel(connection: connection, key: key)
    }
}

extension UUID {
    var networkData: Data {
        var value = uuid
        return withUnsafeBytes(of: &value) { Data($0) }
    }

    init?(networkData: Data) {
        guard networkData.count == 16 else { return nil }
        self = networkData.withUnsafeBytes { bytes in
            NSUUID(uuidBytes: bytes.bindMemory(to: UInt8.self).baseAddress!) as UUID
        }
    }
}

extension Data {
    init(uint16 value: UInt16) {
        self = Data([UInt8((value >> 8) & 0xff), UInt8(value & 0xff)])
    }

    func readUInt16(at offset: Int) -> UInt16 {
        (UInt16(self[offset]) << 8) | UInt16(self[offset + 1])
    }
}
