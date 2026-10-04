# Builds artifacts/package/Tropicast-Station-<version>-win-x64.msix
# Requires the FFmpeg bundle (scripts/build-ffmpeg.sh win-x64) and the Windows SDK (makeappx/signtool).
#
# Signing (an MSIX cannot be installed unsigned):
#   TC_WINDOWS_PFX_PATH / TC_WINDOWS_PFX_PASSWORD  sign with this certificate; TC_MSIX_PUBLISHER must equal its subject
#   TC_REQUIRE_TRUSTED_CHAIN=1                     fail when the certificate does not chain to a trusted root (use with a public CA)
#   -TestSign                                      sign with a throw-away self-signed cert (CI install test only)
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [switch]$TestSign
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$rid = 'win-x64'
$work = Join-Path $root 'artifacts/package/win-x64'
$layout = Join-Path $work 'layout'
$out = Join-Path $root 'artifacts/package'
$publisher = if ($env:TC_MSIX_PUBLISHER) { $env:TC_MSIX_PUBLISHER } else { 'CN=Tropicast' }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be major.minor.patch, got '$Version'." }
$msixVersion = "$Version.0"

if (-not (Test-Path (Join-Path $root "artifacts/ffmpeg/$rid/ffmpeg.exe"))) {
    throw "Missing FFmpeg bundle: run scripts/build-ffmpeg.sh $rid first."
}

function Find-SdkTool([string]$name) {
    $tool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\$name" -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Directory.Parent.Name) } -Descending | Select-Object -First 1
    if (-not $tool) { throw "$name not found; install the Windows 10/11 SDK." }
    $tool.FullName
}

Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $layout, $out | Out-Null
dotnet publish (Join-Path $root 'src/Tropicast.Station.App') -c Release -r $rid --self-contained true "-p:Version=$Version" -o $layout
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
if (-not (Test-Path (Join-Path $layout 'ffmpeg/ffmpeg.exe'))) { throw 'FFmpeg was not included in the publish output.' }
if (-not (Test-Path (Join-Path $layout 'ffmpeg/THIRD_PARTY_NOTICES'))) { throw 'FFmpeg notices were not included.' }
Copy-Item (Join-Path $root 'THIRD_PARTY_NOTICES') (Join-Path $layout 'THIRD_PARTY_NOTICES')
Copy-Item (Join-Path $root 'packaging/windows/Assets') (Join-Path $layout 'Assets') -Recurse

$cert = $null
if ($TestSign) {
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature `
        -FriendlyName 'Tropicast Station test signing' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    Export-Certificate -Cert $cert -FilePath (Join-Path $out 'tropicast-test-signing.cer') | Out-Null
}

(Get-Content (Join-Path $root 'packaging/windows/AppxManifest.xml') -Raw).
    Replace('@PUBLISHER@', [Security.SecurityElement]::Escape($publisher)).
    Replace('@VERSION@', $msixVersion) |
    Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding utf8

$package = Join-Path $out "Tropicast-Station-$Version-$rid.msix"
Remove-Item $package -Force -ErrorAction SilentlyContinue
& (Find-SdkTool 'makeappx.exe') pack /d $layout /p $package /o
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed.' }

$signtool = Find-SdkTool 'signtool.exe'
if ($TestSign) {
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $package
} elseif ($env:TC_WINDOWS_PFX_PATH) {
    & $signtool sign /fd SHA256 /f $env:TC_WINDOWS_PFX_PATH /p $env:TC_WINDOWS_PFX_PASSWORD `
        /tr http://timestamp.digicert.com /td SHA256 $package
} else {
    Write-Warning "$package is NOT signed and cannot be installed until it is signed with a trusted certificate."
    Write-Output "Built $package"
    return
}
if ($LASTEXITCODE -ne 0) { throw 'signtool failed.' }
if (-not $TestSign) {
    # signtool sign already rejects a publisher that differs from the manifest, so verify only checks
    # integrity and trust. A self-signed or private-CA root is not trusted on the runner: that is expected
    # (users import the certificate), so warn. Anything else, such as a tampered file, still fails.
    $ErrorActionPreference = 'Continue'
    $verify = & $signtool verify /pa $package 2>&1 | Out-String
    $verifyExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    Write-Output $verify
    if ($verifyExit -ne 0) {
        $untrustedRoot = $verify -match 'terminated in a root certificate which is not trusted'
        if ($untrustedRoot -and $env:TC_REQUIRE_TRUSTED_CHAIN -ne '1') {
            Write-Warning 'The signing certificate does not chain to a trusted root (self-signed or private CA). The MSIX installs only on machines that trust this certificate; set TC_REQUIRE_TRUSTED_CHAIN=1 to make this an error.'
        } else {
            throw 'The MSIX signature did not verify.'
        }
    }
}
Write-Output "Built $package"
