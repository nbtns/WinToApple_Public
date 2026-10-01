import Foundation
import Network

final class BufferedConnection: @unchecked Sendable {
    private let connection: NWConnection
    private var buffered = Data()

    init(_ connection: NWConnection) {
        self.connection = connection
    }

    func start() async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            var resumed = false
            connection.stateUpdateHandler = { state in
                guard !resumed else { return }
                switch state {
                case .ready:
                    resumed = true
                    continuation.resume()
                case .failed(let error):
                    resumed = true
                    continuation.resume(throwing: error)
                case .cancelled:
                    resumed = true
                    continuation.resume(throwing: TransferError.connectionClosed)
                default:
                    break
                }
            }
            connection.start(queue: DispatchQueue(label: "localbridge.phase1.connection", qos: .userInitiated))
        }
    }

    func readExactly(_ count: Int) async throws -> Data {
        guard count >= 0 else { throw TransferError.invalidMessage }
        while buffered.count < count {
            let incoming = try await receiveSome(maximum: max(64 * 1024, count - buffered.count))
            guard !incoming.isEmpty else { throw TransferError.connectionClosed }
            buffered.append(incoming)
        }

        let result = buffered.prefix(count)
        buffered.removeFirst(count)
        return Data(result)
    }

    func send(_ data: Data) async throws {
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            connection.send(content: data, completion: .contentProcessed { error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume()
                }
            })
        }
    }

    private func receiveSome(maximum: Int) async throws -> Data {
        try await withCheckedThrowingContinuation { continuation in
            connection.receive(minimumIncompleteLength: 1, maximumLength: maximum) { data, _, isComplete, error in
                if let error {
                    continuation.resume(throwing: error)
                } else if let data, !data.isEmpty {
                    continuation.resume(returning: data)
                } else if isComplete {
                    continuation.resume(throwing: TransferError.connectionClosed)
                } else {
                    continuation.resume(returning: Data())
                }
            }
        }
    }
}
