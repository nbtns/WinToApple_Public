# Web QR転送仕様

## セッション

右クリック要求ごとにWindowsが一時Webサーバーを開始します。URLは次の形式です。

```text
http://<private-ip>:<ephemeral-port>/s/<256-bit-base64url-token>/
```

トークンは30分で失効します。正しいトークンで最初に一覧を開いたIPアドレスを記録し、以後は同じIPだけを受け付けます。

## 画面

一覧ページはファイル本文を自動取得しません。

- 画像・動画: `写真アプリに保存する`と`ファイルアプリに保存する`
- その他: `ファイルアプリに保存する`
- フォルダー: Windows側でZIP化し、Files用として表示

写真用ボタンは、画像の場合はプレビューページ、動画の場合はダウンロードと保存の案内ページへ移動します。動画の案内ページはメディアを自動取得せず、`download/{id}`への明示的なリンクを表示します。利用者がダウンロード完了後に動画ファイルを開き、その共有メニューから「ビデオを保存」を選びます。動画を埋め込んだHTMLページ自体の共有は使用しません。

通常のLAN内HTTPは[Web Share API](https://www.w3.org/TR/web-share/)が要求するsecure contextではないため、`navigator.share({ files })`には依存しません。動画は既存のattachment応答で、元のファイル名・拡張子と動画MIMEを保って取得します。動画のコーデック変換や写真アプリへの自動登録は行いません。

## エンドポイント

- `GET /s/{token}/`: 保存方法一覧
- `GET /s/{token}/preview/{id}`: 写真・動画の保存案内
- `GET /s/{token}/content/{id}`: inline media、Range対応
- `GET /s/{token}/download/{id}`: attachment、Range対応
- `POST /s/{token}/complete`: セッション終了

全応答にキャッシュ禁止、Referrer禁止、MIME sniffing禁止、frame禁止、限定CSPを付与します。
