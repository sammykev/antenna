<#
.SYNOPSIS
  Creates a self-signed code-signing certificate for Antenna releases.

.DESCRIPTION
  Run once on your PC in PowerShell. It creates a certificate valid for 5 years and writes:
    antenna-signing.pfx      private key + certificate (keep this secret; back it up)
    antenna-signing.cer      public certificate (safe to share; install it to trust Antenna)
    antenna-signing.pfx.txt  the .pfx as base64, to paste into the SIGNING_CERT_PFX GitHub secret

  Then, in the GitHub repo: Settings > Secrets and variables > Actions > New repository secret:
    SIGNING_CERT_PFX       = contents of antenna-signing.pfx.txt
    SIGNING_CERT_PASSWORD  = the password you typed below

  Self-signed means Windows only trusts it on PCs where you install antenna-signing.cer
  (see -Trust). Other PCs still see a SmartScreen "unknown publisher" warning.

.PARAMETER Trust
  Also trusts the certificate on this PC (asks for administrator rights).
#>
param(
    [string]$OutDir = ".",
    [switch]$Trust
)

$ErrorActionPreference = "Stop"
$password = Read-Host "Choose a password for the certificate" -AsSecureString

$cert = New-SelfSignedCertificate -Type CodeSigningCert `
    -Subject "CN=Okechukwu Nkwocha, O=Antenna" `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -NotAfter (Get-Date).AddYears(5) `
    -CertStoreLocation Cert:\CurrentUser\My

$pfx = Join-Path $OutDir "antenna-signing.pfx"
$cer = Join-Path $OutDir "antenna-signing.cer"
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $password | Out-Null
Export-Certificate -Cert $cert -FilePath $cer | Out-Null
[Convert]::ToBase64String([IO.File]::ReadAllBytes((Resolve-Path $pfx))) | Set-Content -NoNewline (Join-Path $OutDir "antenna-signing.pfx.txt")

# The private key now lives in the .pfx; keep the store copy out of the way.
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"

Write-Host "Created $pfx, $cer and antenna-signing.pfx.txt (thumbprint $($cert.Thumbprint))."

if ($Trust) {
    $resolved = (Resolve-Path $cer).Path
    Start-Process powershell -Verb RunAs -Wait -ArgumentList @(
        "-NoProfile", "-Command",
        "Import-Certificate -FilePath '$resolved' -CertStoreLocation Cert:\LocalMachine\Root | Out-Null; " +
        "Import-Certificate -FilePath '$resolved' -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null"
    )
    Write-Host "This PC now trusts Antenna releases signed with this certificate."
}
