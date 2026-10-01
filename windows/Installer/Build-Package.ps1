[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '0.3.2.0',
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipSigning
)

$ErrorActionPreference = 'Stop'
$installerDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = Resolve-Path (Join-Path $installerDirectory '..\..')
$artifactsDirectory = Join-Path $repositoryRoot 'artifacts'
$layoutDirectory = Join-Path $artifactsDirectory "package-layout-$Version"
$packagePath = Join-Path $artifactsDirectory "LocalBridge-$Version-x64.msix"
$certificatePath = Join-Path $artifactsDirectory 'LocalBridge-Development.cer'
$windowsKit = 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.22621.0\x64'
$makeAppx = Join-Path $windowsKit 'makeappx.exe'
$signTool = Join-Path $windowsKit 'signtool.exe'
$msbuild = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe'

foreach ($required in @($makeAppx, $signTool, $msbuild)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "Required build tool not found: $required" }
}

if (Test-Path -LiteralPath $layoutDirectory) {
    $resolvedLayout = (Resolve-Path -LiteralPath $layoutDirectory).Path
    $resolvedArtifacts = (Resolve-Path -LiteralPath $artifactsDirectory).Path
    if (-not $resolvedLayout.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The package staging directory is outside the artifacts directory.'
    }
    Remove-Item -LiteralPath $layoutDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $layoutDirectory -Force | Out-Null

$settingsProject = Join-Path $repositoryRoot 'windows\SettingsApp\LocalBridge.Settings.csproj'
$agentProject = Join-Path $repositoryRoot 'windows\BackgroundAgent\LocalBridge.Agent.csproj'
$nugetConfig = Join-Path $repositoryRoot 'NuGet.Config'
& dotnet restore $settingsProject -r win-x64 --configfile $nugetConfig
if ($LASTEXITCODE -ne 0) { throw 'Failed to restore the settings app.' }
& dotnet restore $agentProject -r win-x64 --configfile $nugetConfig
if ($LASTEXITCODE -ne 0) { throw 'Failed to restore the background agent.' }
& dotnet publish $settingsProject -c $Configuration -r win-x64 --self-contained true --no-restore -o $layoutDirectory
if ($LASTEXITCODE -ne 0) { throw 'Failed to publish the settings app.' }
& dotnet publish $agentProject -c $Configuration -r win-x64 --self-contained true --no-restore -o $layoutDirectory
if ($LASTEXITCODE -ne 0) { throw 'Failed to publish the background agent.' }

$shellProject = Join-Path $repositoryRoot 'windows\ShellExtension\LocalBridge.ShellExtension.vcxproj'
$devCommand = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat'
$nativeBuildCommand = "set Path=&& call `"$devCommand`" -arch=x64 -host_arch=x64 && msbuild `"$shellProject`" /m /p:Configuration=$Configuration /p:Platform=x64 /verbosity:minimal"
& $env:ComSpec /d /s /c $nativeBuildCommand
if ($LASTEXITCODE -ne 0) { throw 'Failed to build the Explorer command.' }
$shellDll = Join-Path $repositoryRoot "windows\ShellExtension\bin\$Configuration\x64\LocalBridge.ShellExtension.dll"
Copy-Item -LiteralPath $shellDll -Destination (Join-Path $layoutDirectory 'LocalBridge.ShellExtension.dll')

$manifestTemplate = Get-Content -LiteralPath (Join-Path $installerDirectory 'AppxManifest.template.xml') -Raw -Encoding UTF8
$manifest = $manifestTemplate.Replace('__VERSION__', $Version)
[IO.File]::WriteAllText((Join-Path $layoutDirectory 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

Add-Type -AssemblyName System.Drawing
$assetsDirectory = Join-Path $layoutDirectory 'Assets'
New-Item -ItemType Directory -Path $assetsDirectory -Force | Out-Null
function New-LocalBridgeLogo([string]$Path, [int]$Width, [int]$Height) {
    $bitmap = [Drawing.Bitmap]::new($Width, $Height)
    try {
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.Clear([Drawing.Color]::FromArgb(22, 117, 91))
            $penWidth = [Math]::Max(2, [Math]::Min($Width, $Height) / 12)
            $pen = [Drawing.Pen]::new([Drawing.Color]::White, $penWidth)
            try {
                $margin = [Math]::Min($Width, $Height) * 0.22
                $graphics.DrawLine($pen, $margin, $Height / 2, $Width - $margin, $Height / 2)
                $graphics.DrawLine($pen, $Width - $margin * 1.5, $Height / 2 - $margin / 2, $Width - $margin, $Height / 2)
                $graphics.DrawLine($pen, $Width - $margin * 1.5, $Height / 2 + $margin / 2, $Width - $margin, $Height / 2)
            } finally { $pen.Dispose() }
        } finally { $graphics.Dispose() }
        $bitmap.Save($Path, [Drawing.Imaging.ImageFormat]::Png)
    } finally { $bitmap.Dispose() }
}
New-LocalBridgeLogo (Join-Path $assetsDirectory 'StoreLogo.png') 50 50
New-LocalBridgeLogo (Join-Path $assetsDirectory 'Square44x44Logo.png') 44 44
New-LocalBridgeLogo (Join-Path $assetsDirectory 'Square150x150Logo.png') 150 150
New-LocalBridgeLogo (Join-Path $assetsDirectory 'Wide310x150Logo.png') 310 150

if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
& $makeAppx pack /d $layoutDirectory /p $packagePath /o
if ($LASTEXITCODE -ne 0) { throw 'Failed to create the MSIX package.' }

if (-not $SkipSigning) {
    $certificate = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq 'CN=LocalBridge Development' | Select-Object -First 1
    if (-not $certificate) {
        $certificate = New-SelfSignedCertificate `
            -Type Custom `
            -Subject 'CN=LocalBridge Development' `
            -KeyUsage DigitalSignature `
            -FriendlyName 'LocalBridge Development Package Signing' `
            -CertStoreLocation 'Cert:\CurrentUser\My' `
            -TextExtension @('2.5.29.19={text}CA=false', '2.5.29.37={text}1.3.6.1.5.5.7.3.3')
    }
    Export-Certificate -Cert $certificate -FilePath $certificatePath -Force | Out-Null
    & $signTool sign /fd SHA256 /sha1 $certificate.Thumbprint $packagePath
    if ($LASTEXITCODE -ne 0) { throw 'Failed to sign the MSIX package.' }
}

Write-Host "Package created: $packagePath"
if ($SkipSigning) {
    Write-Host 'This package is unsigned and intended only for build validation.'
} else {
    Write-Host "Install: .\Install.ps1 -PackagePath `"$packagePath`""
}
