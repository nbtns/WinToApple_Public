# 実験用の旧ネイティブ受信モード

標準方式は、Windowsで表示した一時QRをiPhoneのカメラで読み、Safariで受信するWeb方式です。

このリポジトリには、それ以前に開発したiPhoneアプリによるネイティブ受信と暗号化通信の実験コードも含まれます。設計の検討過程を参照できるよう保持しており、完成済みの代替モードとして案内するものではありません。

## コード

- `ios/LocalBridge`: SwiftによるiPhoneアプリ
- `windows/Protocol`: LBP1/LBP2の通信・暗号化処理と共通コード
- `windows/Phase1Sender`: 旧通信方式を試すコマンドライン送信ツール
- `windows/Protocol.Tests`: 通信処理の自動テスト
- `windows/WindowsShared`: 機器登録・秘密鍵保存などを含むWindows共通処理

Windows側にはProtocolやWindowsSharedへの参照があります。公開版では元の依存関係と配置を保ち、単純なフォルダー削除によるビルド不整合を避けています。

## 資料

- [旧LBP1の実行・テスト手順](phase1-runbook.md)
- [旧iPhoneアプリの機器登録](pairing.md)
- [ネイティブ通信仕様](../protocol/protocol.md)
- [メッセージ形式](../protocol/message-schema.md)

これらの説明は標準Web受信の操作手順ではありません。通常の利用は[導入・実機テスト手順](runbook.md)、現在の通信仕様は[Web QR転送仕様](../protocol/web-transfer.md)を参照してください。
