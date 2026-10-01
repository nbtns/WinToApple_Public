import SwiftUI
import UniformTypeIdentifiers

struct ContentView: View {
    @EnvironmentObject private var receiver: ReceiverViewModel

    var body: some View {
        NavigationStack {
            ScrollView {
                VStack(spacing: 22) {
                    Image(systemName: receiver.isReady ? "checkmark.circle.fill" : (receiver.isPaired ? "wifi.exclamationmark" : "iphone.and.arrow.forward"))
                        .font(.system(size: 72))
                        .foregroundStyle(receiver.isReady ? .green : .orange)

                    Text(receiver.statusTitle)
                        .font(.largeTitle.bold())
                        .multilineTextAlignment(.center)

                    Text(receiver.statusDetail)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)

                    if receiver.isPaired {
                        pairedSection
                    } else {
                        pairingSection
                    }

                    if !receiver.receivedFiles.isEmpty { receivedSection }

                    Text("この画面を開いている間だけ受信します。受信中は自動ロックを一時的に防ぎます。")
                        .font(.footnote)
                        .foregroundStyle(.secondary)
                        .multilineTextAlignment(.center)
                }
                .padding(24)
            }
            .navigationTitle("LocalBridge")
            .fileImporter(
                isPresented: $receiver.isFolderPickerPresented,
                allowedContentTypes: [.folder],
                allowsMultipleSelection: false,
                onCompletion: receiver.applyFolderSelection
            )
        }
    }

    private var pairingSection: some View {
        VStack(spacing: 16) {
            Text("初回の機器登録")
                .font(.title2.bold())
            QRCodeView(text: "localbridge-pair://\(receiver.pairingCode.replacingOccurrences(of: "-", with: ""))")
                .frame(width: 190, height: 190)
            Text(receiver.pairingCode)
                .font(.system(.headline, design: .monospaced))
                .textSelection(.enabled)
                .multilineTextAlignment(.center)
            Text("WindowsのLocalBridge設定を開き、このコードを入力してください。登録後は毎回の確認は不要です。")
                .font(.footnote)
                .foregroundStyle(.secondary)
                .multilineTextAlignment(.center)
        }
        .padding()
        .frame(maxWidth: .infinity)
        .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 18))
    }

    private var pairedSection: some View {
        VStack(alignment: .leading, spacing: 14) {
            Label("登録済み: \(receiver.pairedDeviceName ?? "Windows")", systemImage: "desktopcomputer")
                .font(.headline)
            if let port = receiver.port {
                Text("待受ポート: \(port)")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
            }
            Button {
                receiver.chooseSaveFolder()
            } label: {
                Label(
                    receiver.saveFolderName.map { "Files保存先: \($0)" } ?? "Filesの保存先を選ぶ",
                    systemImage: "folder"
                )
            }
            Text("写真・動画は写真アプリへ、その他は選択したFilesフォルダへ自動保存します。保存できない場合はアプリ内受信箱へ残します。")
                .font(.footnote)
                .foregroundStyle(.secondary)
            Toggle("受信画面の自動ロックを防ぐ", isOn: $receiver.preventAutoLock)
            Button("機器登録を解除", role: .destructive) { receiver.unpair() }
        }
        .padding()
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 18))
    }

    private var receivedSection: some View {
        VStack(alignment: .leading, spacing: 10) {
            Text("今回受信したファイル").font(.headline)
            ForEach(receiver.receivedFiles, id: \.self) { name in
                Label(name, systemImage: "doc.fill").lineLimit(1)
            }
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .padding()
        .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 16))
    }
}

#Preview {
    ContentView().environmentObject(ReceiverViewModel(preview: true))
}
