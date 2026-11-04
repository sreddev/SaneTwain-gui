<#
.SYNOPSIS
  Creates a self-signed certificate for installing test MSIX builds on this PC.
  Store builds don't need this: Microsoft signs them.

  The certificate must be trusted before Windows installs a package signed with it. That step
  needs an administrator PowerShell; this script prints the command instead of running it.
#>
param(
    [string]$Publisher = "CN=Scan Center Dev",
    [string]$Password = "scancenter"
)
$ErrorActionPreference = "Stop"
$pfx = Join-Path $PSScriptRoot "dev-cert.pfx"
$cer = Join-Path $PSScriptRoot "dev-cert.cer"
$cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
    -FriendlyName "Scan Center developer signing" -CertStoreLocation "Cert:\CurrentUser\My" `
    -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}") -NotAfter (Get-Date).AddYears(3)
$pw = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pw | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
Write-Host "Created $pfx and $cer"
Write-Host ""
Write-Host "To trust it (once, in an administrator PowerShell):"
Write-Host "  Import-Certificate -FilePath `"$cer`" -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
