<#
.SYNOPSIS
  Builds Scan Center as an MSIX package (for the Microsoft Store or for side-loading).

.EXAMPLE
  # Local test package, signed with a self-made certificate (see make-dev-cert.ps1)
  .\build-msix.ps1 -Sign

.EXAMPLE
  # Store upload: use the identity from Partner Center > Product identity. The Store signs it.
  .\build-msix.ps1 -IdentityName "12345Contoso.ScanCenter" -Publisher "CN=ABCD1234-..." -PublisherDisplayName "Contoso" -Version 1.0.1.0
#>
param(
    [string]$Version = "1.0.0.0",
    [ValidateSet("x64", "arm64")] [string]$Arch = "x64",
    [string]$IdentityName = "ScanCenter.Dev",
    [string]$Publisher = "CN=Scan Center Dev",
    [string]$PublisherDisplayName = "Scan Center (developer build)",
    [string]$DisplayName = "SaneTwain Scan Center",
    [switch]$Sign,
    [string]$CertPath = "$PSScriptRoot\dev-cert.pfx",
    [string]$CertPassword = "scancenter"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "artifacts"
$layout = Join-Path $out "layout-$Arch"
$msix = Join-Path $out "ScanCenter_$($Version)_$Arch.msix"

# Windows SDK tools (newest installed version)
$sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName "x64\makeappx.exe") } |
    Sort-Object { [version]$_.Name } | Select-Object -Last 1
if (-not $sdkBin) { throw "Windows SDK not found. Install it with: winget install Microsoft.WindowsSDK.10.0.26100" }
$tools = Join-Path $sdkBin.FullName "x64"
Write-Host "Windows SDK tools: $tools"

# 1. Publish a self-contained build (the Store can't install the .NET runtime for a desktop app)
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
dotnet publish "$root\src\ScanCenter.App\ScanCenter.App.csproj" -c Release -r "win-$Arch" --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -p:Version=$($Version -replace '\.\d+$','') -o $layout
if ($LASTEXITCODE) { throw "dotnet publish failed" }

# 2. Manifest and logos
Copy-Item "$PSScriptRoot\Assets" (Join-Path $layout "Assets") -Recurse -Force
Remove-Item (Join-Path $layout "Assets\StoreListing-512.png") -ErrorAction SilentlyContinue
$manifest = (Get-Content "$PSScriptRoot\Package.appxmanifest" -Raw).
    Replace('$IDENTITY_NAME$', $IdentityName).Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($Publisher)).
    Replace('$VERSION$', $Version).Replace('$ARCH$', $Arch).
    Replace('$DISPLAY_NAME$', [Security.SecurityElement]::Escape($DisplayName)).
    Replace('$PUBLISHER_DISPLAY_NAME$', [Security.SecurityElement]::Escape($PublisherDisplayName))
Set-Content (Join-Path $layout "AppxManifest.xml") $manifest -Encoding UTF8

# 3. Resource index (lets Windows pick the right logo sizes)
Push-Location $layout
& "$tools\makepri.exe" createconfig /cf priconfig.xml /dq en-US /o | Out-Null
& "$tools\makepri.exe" new /pr . /cf priconfig.xml /of resources.pri /o | Out-Null
Remove-Item priconfig.xml
Pop-Location
if ($LASTEXITCODE) { throw "makepri failed" }

# 4. Pack
& "$tools\makeappx.exe" pack /d $layout /p $msix /o /nv | Select-String -Pattern "error|succeeded" | ForEach-Object { $_.Line }
if ($LASTEXITCODE) { throw "makeappx failed" }

# 5. Sign for side-loading (Store uploads are signed by Microsoft; don't sign those)
if ($Sign) {
    if (-not (Test-Path $CertPath)) { throw "No certificate at $CertPath. Run make-dev-cert.ps1 first." }
    & "$tools\signtool.exe" sign /fd SHA256 /a /f $CertPath /p $CertPassword $msix
    if ($LASTEXITCODE) { throw "signtool failed" }
}
$size = [math]::Round((Get-Item $msix).Length / 1MB, 1)
Write-Host "Built $msix ($size MB)"
