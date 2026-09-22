# Builds the shippable Aiko artifacts into dist\:
#
#   Aiko-<version>-x64.msix   the installer most people should use
#   Aiko-<version>-x64.zip    the same app as a folder, for running without installing
#   Aiko.cer                  the certificate the MSIX is signed with, needed once to install it
#
# Used to cut a GitHub release. The package is laid out by the MSIX tooling in the build itself, because a package
# built by hand out of the unpackaged output cannot resolve the app's XAML under package identity. Signing uses
# signtool from the Microsoft.Windows.SDK.BuildTools package the app already references, with a self-signed
# certificate kept in the current user's store.

[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '1.0.0',
    [string] $Configuration = 'Release',
    [switch] $SkipTests
)

$ErrorActionPreference = 'Stop'

$root     = Split-Path -Parent $PSScriptRoot
$project  = Join-Path $root 'AikoPdf\AikoPdf.csproj'
$manifest = Join-Path $root 'AikoPdf\Package.appxmanifest'
$dist     = Join-Path $root 'dist'
$payload  = Join-Path $root "AikoPdf\bin\x64\$Configuration\net9.0-windows10.0.26100.0\win-x64\publish"
$packages = Join-Path $root 'AikoPdf\AppPackages'

New-Item -ItemType Directory -Force -Path $dist | Out-Null

# Everything below needs the packages restored, including the SDK tools themselves.
Write-Host 'Restoring...' -ForegroundColor Cyan
dotnet restore $project --nologo
if ($LASTEXITCODE -ne 0) { throw 'restore failed' }

# The SDK tools ship inside the NuGet package the app references. Pin that exact version: a machine can hold
# several, and the tools from a newer one are not always the ones this build was tested with.
$toolsVersion = ([xml](Get-Content $project)).Project.ItemGroup.PackageReference |
    Where-Object { $_.Include -eq 'Microsoft.Windows.SDK.BuildTools' } |
    Select-Object -First 1 -ExpandProperty Version
if (-not $toolsVersion) { throw 'the project does not reference Microsoft.Windows.SDK.BuildTools' }
$buildTools = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools\$toolsVersion\bin\*\x64" -Directory -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1
if (-not $buildTools) { throw "Microsoft.Windows.SDK.BuildTools $toolsVersion not restored; run dotnet restore first" }
$signtool = Join-Path $buildTools.FullName 'signtool.exe'

if (-not $SkipTests) {
    Write-Host 'Testing...' -ForegroundColor Cyan
    dotnet test (Join-Path $root 'tests\AikoPdf.Tests\AikoPdf.Tests.csproj') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'tests failed' }
}

# The portable build: a self-contained folder that runs from anywhere.
Write-Host 'Publishing x64...' -ForegroundColor Cyan
if (Test-Path $payload) { Remove-Item -Recurse -Force $payload }
dotnet publish $project -c $Configuration -p:Platform=x64 -r win-x64 --self-contained true `
    -p:Version="$Version.0" -o $payload --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

$zip = Join-Path $dist "Aiko-$Version-x64.zip"
Write-Host "Zipping to $zip..." -ForegroundColor Cyan
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip -CompressionLevel Optimal

# The package: same code, built again with the MSIX tooling so the layout and the resource index carry the
# package identity from Package.appxmanifest.
Write-Host 'Building MSIX...' -ForegroundColor Cyan
# The manifest is a checked-in file, so the version goes in for the build and comes straight back out: a release
# build must not leave the working tree dirty. It is written without a byte order mark whichever PowerShell runs
# this, so the file does not change shape build to build.
$manifestText = Get-Content $manifest -Raw
$stamped      = [regex]::Replace($manifestText, '(?<=Version=")\d+\.\d+\.\d+\.\d+(?=")', "$Version.0")
$utf8         = [Text.UTF8Encoding]::new($false)

if (Test-Path $packages) { Remove-Item -Recurse -Force $packages }
[IO.File]::WriteAllText($manifest, $stamped, $utf8)
try
{
    dotnet build $project -c $Configuration -p:Platform=x64 -p:WindowsPackageType=MSIX `
        -p:GenerateAppxPackageOnBuild=true -p:Version="$Version.0" --nologo
    if ($LASTEXITCODE -ne 0) { throw 'MSIX build failed' }
}
finally
{
    [IO.File]::WriteAllText($manifest, $manifestText, $utf8)
}

$built = Get-ChildItem $packages -Recurse -Filter '*.msix' | Select-Object -First 1
if (-not $built) { throw 'no MSIX was produced' }

$msix = Join-Path $dist "Aiko-$Version-x64.msix"
Copy-Item $built.FullName $msix -Force

# Signing. The certificate's subject has to match the manifest's Publisher exactly.
$subject = 'CN=Devin Green'
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $cert) {
    Write-Host 'Creating a self-signed signing certificate...' -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate -Type Custom -Subject $subject -KeyUsage DigitalSignature `
        -FriendlyName 'Aiko code signing' -CertStoreLocation 'Cert:\CurrentUser\My' `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}') -NotAfter (Get-Date).AddYears(5)
}

Export-Certificate -Cert $cert -FilePath (Join-Path $dist 'Aiko.cer') | Out-Null
# /tr with /td SHA256 asks for an RFC 3161 timestamp; the older /t only ever gives a SHA-1 one.
& $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $msix
if ($LASTEXITCODE -ne 0) { throw 'signtool failed' }

Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table
