import Foundation
import Network

final class Phase1Server: @unchecked Sendable {
    enum State: Sendable {
        case starting
        case ready(UInt16)
        case receiving(String)
        case failed(String)
    }

    var onStateChange: (@Sendable (State) -> Void)?
    var onReceived: (@Sendable (ReceivedFile) -> Void)?

    private let listener: NWListener
    private let identity: LocalDeviceIdentity
    private let pairedDevices: PairedDeviceStore
    private let router: DestinationRouter
    private let queue = DispatchQueue(label: "localbridge.phase1.listener", qos: .userInitiated)
    private let lock = NSLock()
    private var activeConnections: [UUID: NWConnection] = [:]

    init(identity: LocalDeviceIdentity, pairedDevices: PairedDeviceStore, router: DestinationRouter) throws {
        self.identity = identity
        self.pairedDevices = pairedDevices
        self.router = router
        let parameters = NWParameters.tcp
        parameters.includePeerToPeer = true
        listener = try NWListener(using: parameters, on: .any)
        listener.service = NWListener.Service(
            name: "LocalBridge-iPhone",
            type: "_localbridge._tcp"
        )
    }

    func start() {
        onStateChange?(.starting)
        listener.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                if let rawPort = self.listener.port?.rawValue {
                    self.onStateChange?(.ready(rawPort))
                }
            case .failed(let error):
                self.onStateChange?(.failed(Self.friendlyMessage(for: error)))
            case .waiting(let error):
                self.onStateChange?(.failed(Self.friendlyMessage(for: error)))
            default:
                break
            }
        }
        listener.newConnectionHandler = { [weak self] connection in
            self?.accept(connection)
        }
        listener.start(queue: queue)
    }

    func stop() {
        listener.cancel()
        lock.lock()
        let connections = activeConnections.values
        activeConnections.removeAll()
        lock.unlock()
        connections.forEach { $0.cancel() }
    }

    private func accept(_ connection: NWConnection) {
        let id = UUID()
        lock.lock()
        activeConnections[id] = connection
        lock.unlock()

        Task.detached(priority: .userInitiated) { [weak self] in
            guard let self else {
                connection.cancel()
                return
            }
            defer {
                connection.cancel()
                self.lock.lock()
                self.activeConnections[id] = nil
                self.lock.unlock()
                if let port = self.listener.port?.rawValue {
                    self.onStateChange?(.ready(port))
                }
            }

            do {
                let received = try await FileReceiver.receive(
                    connection: connection,
                    identity: self.identity,
                    pairedDevices: self.pairedDevices,
                    router: self.router,
                    onMetadata: { fileName in
                        self.onStateChange?(.receiving(fileName))
                    }
                )
                self.onReceived?(received)
            } catch {
                self.onStateChange?(.failed(error.localizedDescription))
            }
        }
    }

    private static func friendlyMessage(for error: NWError) -> String {
        if case .dns(let code) = error, code == -65570 {
            return "設定の「プライバシーとセキュリティ」からローカルネットワークを許可してください。"
        }
        return "同じWi-Fiとローカルネットワークの許可を確認してください。（\(error.localizedDescription)）"
    }
}
