# WinToApple

**Windowsの右クリックから、iPhoneへファイルを送る個人制作アプリ。**

Windows 11で選んだ写真・動画・資料を、同じ家庭内Wi-Fi上のiPhoneへ送信します。iPhone側はカメラとSafariで受け取り、アプリのインストールやアカウント登録は不要です。アプリ内・コード内の名称は **LocalBridge** です。

## 制作の目的

WindowsからiPhoneへファイルを渡す操作を、普段使うエクスプローラーから始められるようにしました。クラウドへアップロードせず、ファイル選択から受け取りまでを短い操作でつなげることを目指しています。

**ファイルを選択 → 右クリックで「iPhoneに送る」→ QRを読む → Safariで保存方法を選ぶ**

## 主な機能

- Windows 11の右クリックメニューから送信
- 画像・動画・その他のファイルに合わせた保存案内
- 複数ファイルの一覧表示と、フォルダーのZIP送信
- HTTP Rangeによる途中位置からの取得、日本語・絵文字ファイル名への対応

## 使用技術

| 担当する部分 | 技術 |
|---|---|
| Windowsアプリ・設定画面 | C# / .NET 10 / Windows Forms |
| ファイル配信 | ASP.NET Core / Kestrel |
| 右クリック拡張 | C++ / IExplorerCommand |
| iPhoneの受信画面 | Safari / HTML / CSS |
| QRコード・パッケージ | QRCoder / MSIX |
| ソース・秘密情報の検査設定 | GitHub Actions / Semgrep / Gitleaks |

## 設計・実装の工夫

- **Windowsの操作への統合**：右クリック拡張は選択パスの受け渡しに集中させ、ファイル転送やZIP作成を別プロセスへ分離しました。
- **受信側の準備を減らす構成**：iPhoneの標準カメラとSafariで受信できるようにし、画像・動画それぞれの保存操作へ案内します。
- **大容量ファイルへの対応**：ファイル全体をメモリへ読み込まず配信し、HTTP Rangeで途中位置から取得できます。フォルダーは元データを変更せず、一時ZIPとして送ります。
- **アクセスと操作の制御**：セッションごとに256 bitの認証トークンを発行し、30分の期限と最初の接続元IPを確認します。QRを読むだけではダウンロードを開始しません。

## 検証

C#アプリとC++右クリック拡張のReleaseビルド、通信・Web転送の自動テストが通ることを確認しています。QR生成、動画のダウンロード応答、ファイル名、HTTP Range、不正トークンの拒否などを検証しました。

標準方式は信頼できる家庭内Wi-Fi向けのHTTP通信で、通信の暗号化は行いません。iPhone実機での写真アプリへの保存・動画再生は未検証です。

## 設計とコード

- [アーキテクチャ](docs/architecture.md) / [セキュリティ設計](docs/security.md)
- [右クリック拡張](windows/ShellExtension) / [Web転送処理](windows/BackgroundAgent/WebTransferSession.cs) / [Web転送テスト](windows/BackgroundAgent.Tests/Program.cs)
- [検証記録](docs/validation.md) / [ビルド手順](docs/development.md)
