# Phase 1 実行・テスト手順

> この文書は実験用の旧LBP1の通信診断資料です。標準Web方式の送信は[導入・実機テスト手順](runbook.md)を参照してください。標準Web方式では機器登録を使いません。

## 必要なもの

- Windows 11（.NET 10 SDK）
- Xcode 16以降を使えるMac
- iOS 17以降のiPhone
- WindowsとiPhoneが同じ家庭内Wi-Fiに接続されていること

## 1. Windowsコードを確認

リポジトリ直下で実行します。

```powershell
dotnet build .\LocalBridge.slnx
dotnet run --project .\windows\Protocol.Tests\LocalBridge.Protocol.Tests.csproj
```

## 2. iPhoneアプリを入れる

1. このフォルダをMacへコピーまたはGitで取得する
2. `ios/LocalBridge/LocalBridge.xcodeproj`をXcodeで開く
3. TargetのSigning & Capabilitiesで自分のTeamと一意なBundle Identifierを選ぶ
4. iPhoneを接続し、実機を実行先にする
5. アプリを起動し、ローカルネットワークの利用を許可する
6. 画面に「受信できます」とポート番号が出ることを確認する

シミュレータではなく実機を推奨します。アプリは受信中だけ自動ロックを防ぎます。

## 3. Windowsから送る

```powershell
dotnet run --project .\windows\Phase1Sender\LocalBridge.Phase1Sender.csproj -- "C:\path\sample.txt"
```

成功するとWindowsに保存名が表示され、iPhone画面の履歴にも追加されます。ファイルはLocalBridgeアプリのDocuments配下にある`Inbox`へ保存されます。

## 4. 自動検出の診断

自動検出できない場合は、まずルーターの「AP isolation」「端末間通信禁止」「ゲストWi-Fi」を確認します。診断のため、iPhoneの「設定 → Wi-Fi → 接続中ネットワーク」で確認したIPと、LocalBridge画面のポートを指定できます。

```powershell
dotnet run --project .\windows\Phase1Sender\LocalBridge.Phase1Sender.csproj -- "C:\path\sample.txt" --host 192.168.1.20 --port 57321
```

IP指定で成功する場合はBonjour/mDNSの経路だけに問題があります。Windows Defender FirewallでプライベートネットワークのUDP 5353とアプリの送信を許可してください。

## 手動テスト

| 項目 | 操作 | 期待結果 |
|---|---|---|
| 小さいファイル | 1 KBのTXTを送る | 同じ内容でInboxへ保存 |
| 空ファイル | 0 byteのファイルを送る | 0 byteで保存 |
| 日本語 | `録音データ.txt`を送る | 文字化けせず保存 |
| 絵文字 | `完成🎉.txt`を送る | 文字化けせず保存 |
| 同名 | 同じ名前を2回送る | 2回目が` (1)`付きで保存 |
| 大きいファイル | 100 MBを送る | メモリ使用量がファイルサイズに比例せず完了 |
| Wi-Fi切断 | 転送中にWi-Fiを切る | 一時ファイルが完成品として残らず失敗表示 |
| 改ざん | テスト用に送信データを変更 | AES-GCMまたはSHA-256で拒否 |

1 GB以上、中断再開、空き容量不足の詳細試験はPhase 6で行います。
