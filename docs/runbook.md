# 導入・実機テスト手順

## 必要環境

- Windows 11
- Safariを利用できるiPhone
- WindowsとiPhoneが同じ家庭内Wi-Fiに接続されていること

Mac、Xcode、iPhoneアプリは不要です。

これは標準Web方式の利用条件です。ソースからWindowsパッケージを作るには、.NET SDKとC++のビルド環境も必要です。[開発手順](development.md)を参照してください。公開ソースにはMSIXや証明書を同梱していないため、最初にパッケージを作成します。

## 1. Windowsパッケージを作る

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\Installer\Build-Package.ps1
```

## 2. Windowsへ導入する

エクスプローラーでリポジトリ直下の `Install-LocalBridge.cmd` をダブルクリックします。Windowsの管理者確認が表示されたら「はい」を押してください。署名証明書を「ローカル コンピューター」の「信頼されたユーザー」へ登録し、最新のMSIXを自動で導入します。

この操作は、開発用証明書をWindowsの証明書ストアへ追加し、LocalBridgeのMSIXと右クリック機能をインストールします。信頼できるソースから自分で作成したパッケージを使ってください。

MSIXファイルを直接ダブルクリックすると、開発用の自己署名証明書をWindowsがまだ信頼していないため、`0x800B010A` または `0x800B0109` で止まります。必ず `Install-LocalBridge.cmd` を使用してください。

PowerShellから実行する場合:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\Installer\Install.ps1 -PackagePath .\artifacts\LocalBridge-0.3.2.0-x64.msix
```

初回の通信時にWindows Defender Firewallが確認を出した場合は、「プライベートネットワーク」を許可します。公衆ネットワークは許可しません。

## 3. 日常の送信

1. エクスプローラーで送る項目を選ぶ
2. 右クリックして「iPhoneに送る」を押す
3. 小さなQR画面をiPhoneのカメラで読み取る
4. Safariで各ファイルの保存方法を選ぶ
5. 全部終わったら「受信を終了する」を押す

### 写真・動画

- 写真の「写真アプリに保存する」: プレビュー画面から「画像を保存」を選ぶ
- 動画の「写真アプリに保存する」: 次の画面で「動画をダウンロード」を押す → 完了後にSafariのダウンロード一覧で動画を開く → **開いた動画の共有ボタン**`□↑`から「ビデオを保存」を選ぶ。案内ページの共有ボタンは使わない
- 「ファイルアプリに保存する」: SafariのダウンロードとしてFilesへ保存する

### その他のファイル

「ファイルアプリに保存する」を押します。フォルダーは構造を維持したZIPとして保存されます。

## 4. 手動テスト

以下は実機で確認するためのチェック項目と期待結果です。実施済みの結果を示す表ではありません。公開版作成時の検証範囲は[検証記録](validation.md)を参照してください。

更新後は、常駐アプリが停止している状態と起動済みの状態の両方で、右クリックからQR画面が開くことを確認します。設定画面を先に開かずに試してください。

導入済みの右クリックコマンドをテスト用ファイルで呼び出すには、次を実行します。コマンドの成功に加えて、デスクトップにQR画像と「QR起動テスト.txt」が表示されることも確認します。確認後はテスト用QRを閉じてください。

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\Installer\Test-ShellLaunch.ps1
```

| 項目 | 期待結果 |
|---|---|
| QRを読むだけ | Safariに一覧が出るが、まだダウンロードされない |
| JPG／MOV | 写真用とFiles用の2ボタンが出る |
| 写真用ボタン（画像） | プレビューと保存の案内が出る |
| 写真用ボタン（動画） | ダウンロードボタンと3段階の保存手順が出る。案内画面だけでは動画を取得しない |
| MP4／MOV／M4Vの動画保存 | ダウンロード完了後に動画ファイルを開き、その共有から「ビデオを保存」を選んで写真アプリで再生できる（iPhone実機で確認） |
| Files用ボタン | Filesのダウンロードへ進む |
| WAV／PDF | Files用ボタンだけが出る |
| 日本語・絵文字名 | 文字化けせず表示・保存できる |
| 複数選択 | 全項目が1ページに並ぶ |
| フォルダー | ZIPとしてダウンロードできる |
| 大容量動画 | HTTP Rangeで途中位置から取得できる |
| QR URLを別端末で開く | 最初に開いたiPhone以外は拒否される |
| 30分経過 | QRと受信ページが無効になる |

## 5. 削除

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\Installer\Uninstall.ps1
```

履歴も削除する場合だけ`-DeletePairingAndHistory`を付けます。
