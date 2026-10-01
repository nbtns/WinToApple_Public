import Foundation
import SwiftUI
import UIKit

@MainActor
final class ReceiverViewModel: ObservableObject {
    @Published private(set) var isReady = false
    @Published private(set) var statusTitle = "受信を準備しています"
    @Published private(set) var statusDetail = "ローカルネットワークへの接続を確認しています。"
    @Published private(set) var port: UInt16?
    @Published private(set) var receivedFiles: [String] = []
    @Published private(set) var isPaired = false
    @Published private(set) var pairedDeviceName: String?
    @Published private(set) var pairingCode = ""
    @Published private(set) var saveFolderName: String?
    @Published var isFolderPickerPresented = false
    @Published var preventAutoLock: Bool = UserDefaults.standard.object(forKey: "LocalBridge.preventAutoLock") as? Bool ?? true {
        didSet {
            UserDefaults.standard.set(preventAutoLock, forKey: "LocalBridge.preventAutoLock")
            UIApplication.shared.isIdleTimerDisabled = preventAutoLock
        }
    }

    private let pairedDevices = PairedDeviceStore()
    private let router = DestinationRouter()
    private var transferServer: Phase1Server?
    private var pairingServer: PairingServer?
    private var pairingSecret: PairingSecret?

    init(preview: Bool = false) {
        if preview {
            isReady = true
            isPaired = true
            pairedDeviceName = "Windows PC"
            statusTitle = "受信できます"
            statusDetail = "Windows PCと接続済み"
            port = 57321
            receivedFiles = ["録音データ.wav"]
            saveFolderName = "LocalBridge受信箱"
            return
        }

        UIApplication.shared.isIdleTimerDisabled = preventAutoLock
        saveFolderName = DestinationPreferences.folderName
        startServices()
    }

    deinit {
        transferServer?.stop()
        pairingServer?.stop()
    }

    func unpair() {
        pairedDevices.removeAll()
        refreshPairingState()
        statusTitle = "Windowsを登録してください"
        statusDetail = "新しい機器登録コードをWindowsへ入力します。"
        try? pairingSecret?.rotate()
        refreshPairingCode()
    }

    func chooseSaveFolder() { isFolderPickerPresented = true }

    func applyFolderSelection(_ result: Result<[URL], Error>) {
        do {
            guard let url = try result.get().first else { return }
            try DestinationPreferences.saveFolder(url)
            saveFolderName = url.lastPathComponent
            statusDetail = "一般ファイルの保存先を「\(url.lastPathComponent)」に設定しました。"
        } catch {
            statusDetail = "保存先を設定できませんでした: \(error.localizedDescription)"
        }
    }

    private func startServices() {
        do {
            let identity = try LocalDeviceIdentity.loadOrCreate()
            let secret = try PairingSecret()
            pairingSecret = secret
            refreshPairingCode()
            refreshPairingState()

            let pairing = try PairingServer(identity: identity, devices: pairedDevices, pairingSecret: secret)
            pairing.onPaired = { [weak self] _ in
                Task { @MainActor in
                    self?.refreshPairingState()
                    self?.statusTitle = "受信できます"
                    self?.statusDetail = "機器登録が完了しました。Windowsでファイルを右クリックしてください。"
                }
            }
            pairing.onCodeRotated = { [weak self] in
                Task { @MainActor in self?.refreshPairingCode() }
            }
            pairing.onFailure = { [weak self] message in
                Task { @MainActor in self?.statusDetail = message }
            }
            pairing.start()
            pairingServer = pairing

            let transfer = try Phase1Server(identity: identity, pairedDevices: pairedDevices, router: router)
            transfer.onStateChange = { [weak self] state in
                Task { @MainActor in self?.apply(state) }
            }
            transfer.onReceived = { [weak self] file in
                Task { @MainActor in
                    self?.receivedFiles.insert(file.savedName, at: 0)
                    if let self, self.receivedFiles.count > 5 {
                        self.receivedFiles.removeLast(self.receivedFiles.count - 5)
                    }
                }
            }
            transfer.start()
            transferServer = transfer
        } catch {
            statusTitle = "受信を開始できません"
            statusDetail = error.localizedDescription
        }
    }

    private func refreshPairingState() {
        let device = pairedDevices.all().first
        isPaired = device != nil
        pairedDeviceName = device?.displayName
        if device == nil {
            statusTitle = "Windowsを登録してください"
            statusDetail = "下の機器登録コードをWindowsのLocalBridge設定へ入力します。"
        }
    }

    private func refreshPairingCode() {
        pairingCode = pairingSecret?.displayCode() ?? ""
    }

    private func apply(_ state: Phase1Server.State) {
        switch state {
        case .starting:
            isReady = false
            statusTitle = "受信を準備しています"
            statusDetail = "ローカルネットワークへの接続を確認しています。"
        case .ready(let port):
            self.port = port
            isReady = isPaired
            if isPaired {
                statusTitle = "受信できます"
                statusDetail = "\(pairedDeviceName ?? "Windows")と機器登録済みです。"
            }
        case .receiving(let fileName):
            isReady = true
            statusTitle = "受信中"
            statusDetail = fileName
        case .failed(let message):
            isReady = false
            statusTitle = "受信できません"
            statusDetail = message
        }
    }
}
