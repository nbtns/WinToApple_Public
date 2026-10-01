[CmdletBinding()]
param(
    [string]$PackagePath
)

$ErrorActionPreference = 'Stop'
$installerDirectory = Split-Path -Parent $PSCommandPath
$repositoryRoot = (Resolve-Path (Join-Path $installerDirectory '..\..')).Path

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $latestPackage = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'artifacts') -Filter 'LocalBridge-*-x64.msix' -File |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if (-not $latestPackage) {
        throw 'LocalBridge installation package was not found in the artifacts folder.'
    }
    $PackagePath = $latestPackage.FullName
}

$resolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$certificatePath = Join-Path (Split-Path -Parent $resolvedPackage) 'LocalBridge-Development.cer'
if (-not (Test-Path -LiteralPath $certificatePath)) {
    throw "Signing certificate not found: $certificatePath"
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$isAdministrator = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdministrator) {
    $escapedScript = $PSCommandPath.Replace("'", "''")
    $escapedPackage = $resolvedPackage.Replace("'", "''")
    $elevatedCommand = "& '$escapedScript' -PackagePath '$escapedPackage'"
    $encodedCommand = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($elevatedCommand))
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru -ArgumentList @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-EncodedCommand', $encodedCommand
    )
    if ($process.ExitCode -ne 0) {
        throw "LocalBridge installation did not complete. Exit code: $($process.ExitCode)"
    }
    return
}

$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
$signature = Get-AuthenticodeSignature -LiteralPath $resolvedPackage
if (-not $signature.SignerCertificate) {
    throw 'The LocalBridge package is not signed.'
}
if ($signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
    throw 'The signing certificate does not match the LocalBridge package.'
}

$trustedCertificate = Get-ChildItem -Path 'Cert:\LocalMachine\TrustedPeople' |
    Where-Object Thumbprint -eq $certificate.Thumbprint |
    Select-Object -First 1
if (-not $trustedCertificate) {
    Import-Certificate -FilePath $certificatePath -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
}

Add-AppxPackage -Path $resolvedPackage -ForceApplicationShutdown

$package = Get-AppxPackage -Name 'LocalBridge.WinToApple' | Sort-Object Version -Descending | Select-Object -First 1
if (-not $package) { throw 'LocalBridge installation could not be verified.' }
$aumid = "$($package.PackageFamilyName)!LocalBridge"
Start-Process explorer.exe "shell:AppsFolder\$aumid"
Write-Host 'LocalBridge is installed. You can now send files from the Explorer right-click menu.'
