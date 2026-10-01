# LocalBridge Protocol 2 (LBP2)

TCP接続ごとに1ファイルを転送します。全整数はビッグエンディアンです。1つの右クリック要求に複数項目がある場合、常駐エージェントが順番に接続します。

## 制限

- 1ファイル最大: 20 GiB
- メタデータJSON最大: 64 KiB
- 平文レコード最大: 5 MiB
- チャンク: 4 MiB
- 右クリック1回: 最大256項目

## 初回の機器登録

iPhoneは`_localbridge-pair._tcp.local`を広告し、128 bitの一時シークレットを26文字Base32コードとして表示します。WindowsはP-256署名公開鍵、端末ID、時刻、24 byte nonceを送り、コードを鍵とするHMAC-SHA-256を付けます。iPhoneの応答にも同じnonceとHMACを付けます。

成功後、iPhoneはWindows公開鍵をKeychainへ、WindowsはiPhone公開鍵をDPAPI保護ファイルへ保存します。iPhoneはコードを直ちに更新します。時刻差は5分まで、5回失敗したコードも更新します。

## 相互認証ハンドシェイク

1. iPhone → Windows: `LBP2`、iPhone端末ID、長期P-256署名公開鍵、一時P-256 ECDH公開鍵、32 byte nonce、iPhone署名
2. Windowsは端末ID・長期公開鍵の固定値と署名を検証
3. Windows → iPhone: `LBP2`、Windows端末ID、一時P-256 ECDH公開鍵、32 byte nonce、これまでの通信記録全体へのWindows署名
4. iPhoneは登録済みWindows公開鍵で署名を検証
5. 両者がP-256 ECDHを計算し、通信記録のSHA-256を含むHKDF-SHA-256からAES-256鍵を導出

GUIDはRFC 4122のネットワークバイト順、P-256公開鍵は65 byte X9.63 uncompressed、ECDSA署名はDER形式です。

## 暗号化レコード

```text
plaintextLength : UInt32
nonce           : 12 bytes
ciphertext      : plaintextLength bytes
tag             : 16 bytes
```

すべてのメタデータ、ファイルデータ、応答をAES-256-GCMで暗号化・改ざん検知します。

## メッセージ

### Metadata (`0x01`)

```json
{
  "protocolVersion": 2,
  "fileId": "sha256 lowercase hex",
  "fileName": "sample.txt",
  "size": 1234,
  "modifiedUtc": "2026-07-17T00:00:00Z",
  "mimeType": "application/octet-stream",
  "sha256": "sha256 lowercase hex",
  "folderArchive": false
}
```

`fileId`を中断ファイルの識別に使います。`fileName`は表示名だけとして扱い、パス区切りを拒否します。

### Resume (`0x11`)

種別1 byteと受信済みoffset UInt64。Windowsはこの位置から再送します。

### Chunk (`0x02`)

種別1 byte、offset UInt64、最大4 MiBのデータ。offset不一致や申告サイズ超過は拒否します。

### Complete (`0x03`)

種別1 byteのみ。iPhoneは最終サイズとSHA-256を確認してから保存先へ移します。

### Result (`0x10`)

種別1 byte、status 1 byte（0=成功、1=失敗）、UTF-8 JSON。保存名または利用者向けエラーを返します。

## 旧LBP1

`Phase1Sender`、LBP1、LBP2は通信診断と将来のネイティブ受信モード用として残しています。現在の日常送信は[Web QR転送](web-transfer.md)を使用します。
