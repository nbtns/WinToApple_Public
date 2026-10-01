# 旧iPhoneアプリ版の機器登録

> 標準のWeb QR受信では機器登録を使いません。右クリックごとにWindowsが新しい一時QRを表示します。この文書は実験用の旧ネイティブ受信モードの資料で、標準Web方式の設定・送信操作とは別です。位置付けは[experimental.md](experimental.md)を参照してください。

## 利用者の操作

1. iPhoneでLocalBridgeを開く
2. WindowsでLocalBridge設定を開く
3. iPhoneに表示された26文字コードをWindowsへ入力する
4. 「このiPhoneを登録」を押す

成功すると双方に相手の名前が表示されます。それ以降、iPhoneで受信画面を開いておけば、Windowsは右クリックだけで自動送信します。

QRコードもiPhone画面に表示しますが、現在のWindows設定はコード手入力です。カメラ権限や追加ライブラリを不要にするため、MVPではこの経路を正本にしています。

## 保管場所

- Windowsの秘密鍵: `%LOCALAPPDATA%\LocalBridge\identity.json`内でDPAPI保護
- Windowsの登録iPhone情報: `%LOCALAPPDATA%\LocalBridge\pairing.json`
- iPhoneの秘密鍵・登録Windows公開鍵: Keychain

解除するときはWindowsとiPhoneの両方で「登録解除」を実行します。別のWindowsを登録すると、iPhone側は固定送信元を新しい1台へ置き換えます。
