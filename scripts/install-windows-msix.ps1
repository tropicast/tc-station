<#
.SYNOPSIS
  Trusts the signing certificate of a Tropicast MSIX and installs it. For packages signed with a
  self-signed or private-CA certificate (for example release pre-releases).

.DESCRIPTION
  Windows only installs an MSIX whose signing certificate is trusted on the machine. This script:
    1. takes the certificate from -Certificate, or reads it from the MSIX signature;
    2. adds it to LocalMachine\TrustedPeople (needs an elevated PowerShell);
    3. installs the package with Add-AppxPackage.

  Only trust a certificate you created or received from a source you trust. Remove it afterwards
  with -Remove.

.EXAMPLE
  .\install-windows-msix.ps1 -Package .\Tropicast-Station-0.1.0-win-x64.msix

.EXAMPLE
  .\install-windows-msix.ps1 -Package .\Tropicast-Station-0.1.0-win-x64.msix -Certificate .\tropicast.cer

.EXAMPLE
  .\install-windows-msix.ps1 -Package .\Tropicast-Station-0.1.0-win-x64.msix -Remove
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [string]$Certificate,
    # Uninstall the app and remove the certificate from LocalMachine\TrustedPeople.
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = ([Security.Principal.WindowsPrincipal]$identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw 'Run this script from an elevated (Run as administrator) PowerShell: it writes to LocalMachine\TrustedPeople.'
}

if (-not (Test-Path -LiteralPath $Package)) { throw "Package not found: $Package" }
$Package = (Resolve-Path -LiteralPath $Package).Path

if ($Certificate) {
    if (-not (Test-Path -LiteralPath $Certificate)) { throw "Certificate not found: $Certificate" }
    $cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2 (Resolve-Path -LiteralPath $Certificate).Path
} else {
    $signature = Get-AuthenticodeSignature -FilePath $Package
    if (-not $signature.SignerCertificate) {
        throw "$Package has no readable signature. Pass the public certificate with -Certificate <file.cer>."
    }
    $cert = $signature.SignerCertificate
}
Write-Output "Certificate: $($cert.Subject) [$($cert.Thumbprint)]"

$store = New-Object Security.Cryptography.X509Certificates.X509Store 'TrustedPeople', 'LocalMachine'
$store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
try {
    if ($Remove) {
        $app = Get-AppxPackage Tropicast.Station
        if ($app) { Remove-AppxPackage $app.PackageFullName; Write-Output 'Removed Tropicast Station.' }
        $store.Remove($cert)
        Write-Output 'Removed the certificate from LocalMachine\TrustedPeople.'
        return
    }
    if ($store.Certificates.Find('FindByThumbprint', $cert.Thumbprint, $false).Count -eq 0) {
        $store.Add($cert)
        Write-Output 'Added the certificate to LocalMachine\TrustedPeople.'
    } else {
        Write-Output 'The certificate is already trusted.'
    }
} finally {
    $store.Close()
}

Add-AppxPackage -Path $Package
$installed = Get-AppxPackage Tropicast.Station
if (-not $installed) { throw 'The MSIX did not install.' }
Write-Output "Installed Tropicast Station $($installed.Version). Start it from the Start menu."
