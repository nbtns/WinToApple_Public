# Message schema

標準Web受信の正本は[web-transfer.md](web-transfer.md)です。以下は旧LBP2ネイティブ受信の互換・拒否ルールです。

- 未知のJSONフィールドは無視する
- `protocolVersion`のmajorが一致しない接続は拒否する
- バイナリメッセージ種別が未知なら接続を閉じる
- サイズ上限を超える長さを受信前に拒否する
- ファイル名は表示名としてのみ扱い、相対・絶対パスとして解釈しない
