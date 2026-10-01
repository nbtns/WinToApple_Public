# 技術レビュー

## 結論

iPhoneアプリを入れず、右クリック後に毎回QRを読み、Safariで保存方法を選ぶ方式を実装しています。標準Web方式の利用にMacやXcodeは不要です。Windowsのビルド環境は[開発手順](development.md)、確認済みの範囲は[検証記録](validation.md)を参照してください。

## 採用技術

| 部分 | 採用 | 理由 |
|---|---|---|
| Windows常駐処理 | C# / .NET 10 / Kestrel | 大容量Range送信と一時Webサーバーを安定して扱える |
| QR表示 | QRCoder 1.8.0 | URLを端末内だけでQR化できる |
| Windows右クリック | C++ `IExplorerCommand` | Windows 11の新しい右クリック欄へ統合できる |
| Windowsパッケージ | MSIX | 拡張の登録と削除を管理できる |
| iPhone | Safari | アプリ、Mac、Xcodeが不要 |
| 認証 | 256 bit一時トークン + 最初の接続元IP固定 | 毎回QRを読んだ端末だけに限定できる |

## 制約と対策

### 写真アプリへの保存

Safariから利用者の操作なしに写真ライブラリへ追加しません。画像ではプレビューから「画像を保存」を選びます。動画では、案内ページから動画ファイルをダウンロードし、そのファイルを開いた共有メニューから「ビデオを保存」を選びます。自動テストは案内とダウンロード応答を確認し、写真アプリへの保存と再生は実機で別途確認します。

### HTTPS

家庭内IPへ有効な公開証明書を簡単に配布できないため、Web版はHTTPです。URLの推測を防ぐ強い一時トークンと接続元固定を入れていますが、エンドツーエンド暗号化ではありません。信頼できる自宅Wi-Fiだけで使います。

### Windows 11の右クリック

Explorer内ではネットワーク通信をせず、選択パスを名前付きパイプへ渡して即時終了します。Webサーバー、ZIP作成、QR表示は別の常駐プロセスが担当します。

### 大容量ファイル

ファイルをメモリへ一括読込せず、KestrelのRange処理でストリーミングします。フォルダーは送信元を変更せず、一時ZIPをセッション終了時に削除します。

## 公式資料

- [Apple: Understanding local network privacy](https://developer.apple.com/documentation/technotes/tn3179-understanding-local-network-privacy)
- [Microsoft: IExplorerCommand](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-iexplorercommand)
- [MDN: Web Share API](https://developer.mozilla.org/en-US/docs/Web/API/Navigator/share)
