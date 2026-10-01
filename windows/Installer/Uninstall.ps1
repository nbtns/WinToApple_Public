[CmdletBinding()]
param(
    [switch]$DeletePairingAndHistory
)

$ErrorActionPreference = 'Stop'
Get-Process -Name 'LocalBridge.Agent' -ErrorAction SilentlyContinue | Stop-Process -Force
Get-AppxPackage -Name 'LocalBridge.WinToApple' | Remove-AppxPackage

Remove-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'LocalBridgeAgent' -ErrorAction SilentlyContinue
Remove-Item -LiteralPath 'HKCU:\Software\LocalBridge' -Recurse -Force -ErrorAction SilentlyContinue

if ($DeletePairingAndHistory) {
    $dataDirectory = Join-Path $env:LOCALAPPDATA 'LocalBridge'
    if (Test-Path -LiteralPath $dataDirectory) {
        $resolvedData = (Resolve-Path -LiteralPath $dataDirectory).Path
        $expectedParent = (Resolve-Path -LiteralPath $env:LOCALAPPDATA).Path
        if (-not $resolvedData.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase) -or (Split-Path -Leaf $resolvedData) -ne 'LocalBridge') {
            throw 'The LocalBridge data directory could not be verified safely.'
        }
        Remove-Item -LiteralPath $resolvedData -Recurse -Force
    }
    Write-Host 'The app, pairing data, and history were removed.'
} else {
    Write-Host 'The app was removed. Pairing data and history were retained. Use -DeletePairingAndHistory for a complete removal.'
}
