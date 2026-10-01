import CryptoKit
import Foundation
import Network

private struct PairingRequest: Decodable {
    let windowsDeviceId: String
    let windowsDisplayName: String
    let windowsPublicKey: String
    let timestamp: Int64
    let nonce: String
    let mac: String
}

private struct PairingResponse: Encodable {
    let success: Bool
    let message: String
    let iPhoneDeviceId: String?
    let iPhoneDisplayName: String?
    let iPhonePublicKey: String?
    let requestNonce: String
    let mac: String
}

final class PairingServer: @unchecked Sendable {
    var onPaired: (@Sendable (PairedWindowsDevice) -> Void)?
    var onCodeRotated: (@Sendable () -> Void)?
    var onFailure: (@Sendable (String) -> Void)?

    private let identity: LocalDeviceIdentity
    private let devices: PairedDeviceStore
    private let pairingSecret: PairingSecret
    private let listener: NWListener
    private let queue = DispatchQueue(label: "localbridge.pairing.listener", qos: .userInitiated)
    private let attemptsLock = NSLock()
    private var failedAttempts = 0

    init(identity: LocalDeviceIdentity, devices: PairedDeviceStore, pairingSecret: PairingSecret) throws {
        self.identity = identity
        self.devices = devices
        self.pairingSecret = pairingSecret
        listener = try NWListener(using: .tcp, on: .any)
        listener.service = NWListener.Service(name: "LocalBridge-Pairing", type: "_localbridge-pair._tcp")
    }

    func start() {
        listener.stateUpdateHandler = { [weak self] state in
            if case .failed(let error) = state { self?.onFailure?(error.localizedDescription) }
        }
        listener.newConnectionHandler = { [weak self] connection in
            Task.detached(priority: .userInitiated) { await self?.handle(connection) }
        }
        listener.start(queue: queue)
    }

    func stop() { listener.cancel() }

    private func handle(_ connection: NWConnection) async {
        defer { connection.cancel() }
        do {
            let buffered = BufferedConnection(connection)
            try await buffered.start()
            let magic = try await buffered.readExactly(8)
            guard magic == Data("LBP2PAIR".utf8) else { throw TransferError.unsupportedProtocol }
            let length = Int((try await buffered.readExactly(4)).readUInt32(at: 0))
            guard length > 0 && length <= 64 * 1024 else { throw TransferError.invalidMessage }
            let request = try JSONDecoder().decode(PairingRequest.self, from: try await buffered.readExactly(length))
            let response = try process(request)
            let encoded = try JSONEncoder().encode(response)
            var frame = Data(uint32: UInt32(encoded.count))
            frame.append(encoded)
            try await buffered.send(frame)
            if response.success,
               let device = devices.find(deviceId: UUID(uuidString: request.windowsDeviceId) ?? UUID()) {
                try pairingSecret.rotate()
                onCodeRotated?()
                onPaired?(device)
            }
        } catch {
            registerFailure()
            onFailure?(error.localizedDescription)
        }
    }

    private func process(_ request: PairingRequest) throws -> PairingResponse {
        guard let windowsId = UUID(uuidString: request.windowsDeviceId),
              abs(Date().timeIntervalSince1970 - TimeInterval(request.timestamp)) <= 300,
              Data(base64Encoded: request.nonce)?.count == 24,
              let windowsPublicData = Data(base64Encoded: request.windowsPublicKey),
              let requestMac = Data(base64Encoded: request.mac) else {
            throw TransferError.invalidMessage
        }
        _ = try P256.Signing.PublicKey(x963Representation: windowsPublicData)
        let name = sanitizeName(request.windowsDisplayName)
        let canonical = PairingServer.canonicalRequest(
            deviceId: windowsId,
            name: name,
            publicKey: request.windowsPublicKey,
            timestamp: request.timestamp,
            nonce: request.nonce
        )
        let key = SymmetricKey(data: pairingSecret.currentData())
        guard HMAC<SHA256>.isValidAuthenticationCode(requestMac, authenticating: Data(canonical.utf8), using: key) else {
            throw PairingError.codeMismatch
        }

        let device = PairedWindowsDevice(
            deviceId: windowsId.uuidString.lowercased(),
            displayName: name,
            publicKey: request.windowsPublicKey,
            pairedAt: Date()
        )
        try devices.save(device)
        attemptsLock.lock()
        failedAttempts = 0
        attemptsLock.unlock()

        let unsigned = PairingResponse(
            success: true,
            message: "機器登録が完了しました。",
            iPhoneDeviceId: identity.deviceId.uuidString.lowercased(),
            iPhoneDisplayName: identity.displayName,
            iPhonePublicKey: identity.signingKey.publicKey.x963Representation.base64EncodedString(),
            requestNonce: request.nonce,
            mac: ""
        )
        let responseMac = HMAC<SHA256>.authenticationCode(
            for: Data(Self.canonicalResponse(unsigned).utf8),
            using: key
        )
        return PairingResponse(
            success: unsigned.success,
            message: unsigned.message,
            iPhoneDeviceId: unsigned.iPhoneDeviceId,
            iPhoneDisplayName: unsigned.iPhoneDisplayName,
            iPhonePublicKey: unsigned.iPhonePublicKey,
            requestNonce: unsigned.requestNonce,
            mac: Data(responseMac).base64EncodedString()
        )
    }

    private func registerFailure() {
        attemptsLock.lock()
        failedAttempts += 1
        let shouldRotate = failedAttempts >= 5
        if shouldRotate { failedAttempts = 0 }
        attemptsLock.unlock()
        if shouldRotate {
            try? pairingSecret.rotate()
            onCodeRotated?()
        }
    }

    private static func canonicalRequest(deviceId: UUID, name: String, publicKey: String, timestamp: Int64, nonce: String) -> String {
        "LBPAIR1\n\(deviceId.uuidString.lowercased())\n\(name)\n\(publicKey)\n\(timestamp)\n\(nonce)"
    }

    private static func canonicalResponse(_ response: PairingResponse) -> String {
        "LBPAIR1-RESPONSE\n\(response.success ? 1 : 0)\n\(response.message)\n\(response.iPhoneDeviceId ?? "")\n\(response.iPhoneDisplayName ?? "")\n\(response.iPhonePublicKey ?? "")\n\(response.requestNonce)"
    }

    private func sanitizeName(_ value: String) -> String {
        let cleaned = value.replacingOccurrences(of: "\n", with: " ").replacingOccurrences(of: "\r", with: " ").trimmingCharacters(in: .whitespaces)
        return String((cleaned.isEmpty ? "Windows PC" : cleaned).prefix(64))
    }
}

enum PairingError: LocalizedError {
    case codeMismatch
    var errorDescription: String? { "機器登録コードが一致しません。" }
}
