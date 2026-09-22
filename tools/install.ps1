# Installs Aiko from the MSIX built by build-release.ps1.
#
# The package is signed with a self-signed certificate, so Windows will not trust it until that certificate is in
# the machine's Trusted People store. Adding it needs administrator rights once; installing the app itself does
# not, and the app lands in your own user account.
#
# Run it from the folder holding Aiko-<version>-x64.msix and Aiko.cer:
#   pwsh -File tools\install.ps1 -Package dist\Aiko-1.0.0-x64.msix

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Package,
    [string] $Certificate
)

$ErrorActionPreference = 'Stop'

$Package = (Resolve-Path -LiteralPath $Package).Path
if (-not $Certificate) { $Certificate = Join-Path (Split-Path -Parent $Package) 'Aiko.cer' }

$signer = (Get-AuthenticodeSignature -LiteralPath $Package).SignerCertificate
if (-not $signer) { throw "The package is not signed: $Package" }

$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
    Where-Object { $_.Thumbprint -eq $signer.Thumbprint }

if (-not $trusted) {
    if (-not (Test-Path -LiteralPath $Certificate)) { throw "Signing certificate not found: $Certificate" }

    # Only ever trust the certificate this package is actually signed with.
    $offered = [Security.Cryptography.X509Certificates.X509Certificate2]::new($Certificate)
    if ($offered.Thumbprint -ne $signer.Thumbprint) {
        throw "$Certificate is not the certificate $Package is signed with; refusing to trust it"
    }

    Write-Host 'Trusting the signing certificate (this asks for administrator rights once)...' -ForegroundColor Cyan
    $command = "Import-Certificate -FilePath '$($Certificate.Replace("'", "''"))' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -ArgumentList '-NoProfile', '-EncodedCommand', $encoded

    $trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople -ErrorAction SilentlyContinue |
        Where-Object { $_.Thumbprint -eq $signer.Thumbprint }
    if (-not $trusted) { throw 'The certificate was not added to Trusted People; nothing was installed' }
}

Write-Host 'Installing Aiko...' -ForegroundColor Cyan
if (Get-AppxPackage -Name 'Artistro08.Aiko') {
    # Replacing a version already installed, and closing it if it happens to be running.
    Add-AppxPackage -Path $Package -ForceApplicationShutdown -ForceUpdateFromAnyVersion
}
else {
    Add-AppxPackage -Path $Package
}

$installed = Get-AppxPackage -Name 'Artistro08.Aiko'
if (-not $installed) { throw 'The package did not install' }

Write-Host "Installed $($installed.Name) $($installed.Version)" -ForegroundColor Green
Write-Host 'Aiko is in the Start menu, and offered for PDFs under Open with.'
