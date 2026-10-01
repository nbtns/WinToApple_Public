# 開発・ビルド手順

## WindowsのC#部分

- Windows 11
- .NET SDK 10.0.300系列（`global.json`は10.0.300、`rollForward`は`latestPatch`）
- 初回のNuGetパッケージ復元に必要なインターネット接続

リポジトリ直下で実行します。

```powershell
dotnet restore .\LocalBridge.slnx --configfile .\NuGet.Config
dotnet build .\LocalBridge.slnx --no-restore -c Release
dotnet run --project .\windows\Protocol.Tests\LocalBridge.Protocol.Tests.csproj --no-build --no-restore -c Release
dotnet run --project .\windows\BackgroundAgent.Tests\LocalBridge.Agent.Tests.csproj --no-build --no-restore -c Release
```

ソリューションのビルド対象はC#の各プロジェクトです。C++の右クリック拡張は、次のパッケージ作成スクリプトから別途ビルドします。

## 右クリック拡張とMSIX

現在のスクリプトは以下のインストール構成を前提とします。

- Visual Studio 2022 Build Tools
- C++ツールセットv143とMSBuild
- Windows SDK 10.0.22621.0の`makeappx.exe`と`signtool.exe`
- 既定パス `C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools`
- SDKツールパス `C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64`

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\Installer\Build-Package.ps1
```

出力先は`artifacts/`です。ソース公開版には生成したMSIX・証明書を含めません。

通常のパッケージ作成では、必要に応じて現在のWindowsユーザーの証明書ストアへ自己署名証明書を作成します。インストールは別の操作で、[導入手順](runbook.md)のスクリプトが証明書の信頼登録とMSIX導入を行います。

署名なしでパッケージ構成だけを確認する場合:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\windows\Installer\Build-Package.ps1 -SkipSigning
```

このコマンドもビルドツールを必要とします。生成物はインストール用の署名済みパッケージではありません。

## 実験用の旧iPhoneアプリ

標準のSafari受信には不要です。実験コードの位置付けと資料は[experimental.md](experimental.md)を参照してください。旧iOSアプリをビルドする場合だけ、Mac、Xcode、実機、利用者自身の署名設定が必要になります。

## 自動検査

GitHub ActionsにはSemgrepとGitleaksを設定しています。リポジトリへ登録後、push・pull request・手動実行で検査します。設定ファイルを同梱していることと、GitHub上で検査が成功したことは別の状態です。

`.gitignore`はビルド出力・診断ログ・証明書・秘密鍵・環境変数ファイルを除外します。既にGitへ登録されたファイルを取り除く機能はないため、新しいファイルの登録前には対象を確認してください。
