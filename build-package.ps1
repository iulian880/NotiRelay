param(
    # Optional: password for the temporary signing PFX. If omitted, you are prompted.
    # The PFX stays on this build PC and is never copied into the deploy folder, so this
    # only protects the local private key.
    [string]$PfxPassword
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$src = Join-Path $root 'src\NotiRelay.csproj'
$out = Join-Path $root 'out'
$portable = Join-Path $out 'portable'
$deploy = Join-Path $out 'deploy'
$manifestStage = Join-Path $out 'identity-stage'
$msix = Join-Path $out 'NotiRelay.identity.msix'
$manifest = Join-Path $root 'Package.appxmanifest'
$publisher = 'CN=NotiRelay Dev'

function Find-SdkTool([string]$name) {
    $programFilesX86 = [Environment]::GetFolderPath('ProgramFilesX86')
    $kitsRoot = Join-Path $programFilesX86 'Windows Kits\10\bin'
    if (-not (Test-Path $kitsRoot)) { return $null }
    Get-ChildItem $kitsRoot -Filter $name -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match '\\x64\\' } |
        Sort-Object { try { [version]$_.Directory.Parent.Name } catch { [version]'0.0' } } -Descending |
        Select-Object -First 1
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK is required on your personal build computer. Do not install it on your work laptop.'
}
$sdkLines = @(& dotnet --list-sdks 2>$null)
if (-not ($sdkLines | Where-Object { $_ -match '^\s*10\.' })) {
    throw 'The .NET 10 SDK is required. Install it on your personal Windows build PC, not on your work laptop.'
}

$makeAppx = Find-SdkTool 'makeappx.exe'
$signTool = Find-SdkTool 'signtool.exe'
if (-not $makeAppx -or -not $signTool) {
    throw 'Windows SDK tools makeappx.exe and signtool.exe were not found. Add the Windows 10/11 SDK to your personal build PC.'
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $portable, $manifestStage -Force | Out-Null

Write-Host 'Publishing a self-contained x64 desktop app...' -ForegroundColor Cyan
& dotnet publish $src -c Release -r win-x64 --self-contained true -o $portable
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Sparse identity package: only the manifest goes into the MSIX.
Copy-Item $manifest (Join-Path $manifestStage 'AppxManifest.xml') -Force
Write-Host 'Packing sparse identity MSIX (application binaries remain in out\portable)...' -ForegroundColor Cyan
& $makeAppx.FullName pack /o /nv /d $manifestStage /p $msix
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed.' }

Write-Host 'Creating a development-only code-signing certificate...' -ForegroundColor Cyan
$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $publisher `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -HashAlgorithm SHA256 `
    -KeyLength 2048 `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(2)

$cerPath = Join-Path $out 'NotiRelay.cer'
$pfxPath = Join-Path $out 'NotiRelay.pfx'
$thumbPath = Join-Path $out 'NotiRelay.thumbprint.txt'
Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
$cert.Thumbprint | Set-Content -Path $thumbPath -Encoding ASCII

# Sign the identity MSIX with the certificate already in this user's store (by thumbprint,
# no password needed).
& $signTool.FullName sign /fd SHA256 /sha1 $cert.Thumbprint /s My $msix
if ($LASTEXITCODE -ne 0) { throw 'SignTool failed to sign the identity MSIX.' }

# Export a portable copy of the signing key (optional; it stays on this PC and is never copied to
# deploy). Only needed to re-sign future builds, so a failure here does not stop the packaging.
try {
    if ($PfxPassword) {
        $securePfx = New-Object System.Security.SecureString
        foreach ($ch in $PfxPassword.ToCharArray()) { $securePfx.AppendChar($ch) }
        $securePfx.MakeReadOnly()
        Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password $securePfx | Out-Null
        Write-Host "Portable signing key: $pfxPath (password-protected)."
    }
    else {
        Export-PfxCertificate -Cert $cert -FilePath $pfxPath -ProtectTo "$env:USERDOMAIN\$env:USERNAME" | Out-Null
        Write-Host "Portable signing key: $pfxPath (protected for the current user)."
    }
}
catch {
    Write-Host "Note: portable PFX export skipped ($($_.Exception.Message))." -ForegroundColor Yellow
    Write-Host 'The signed package was still created. Re-run with -PfxPassword to keep a portable key.'
}

# Trust the development certificate in this build user's profile (TrustedPeople is the store MSIX
# sideload trust uses, the same one the target PC's install script writes to).
Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null

$sig = Get-AuthenticodeSignature -FilePath $msix
if ($null -eq $sig.SignerCertificate) {
    throw "The identity MSIX is not signed: $($sig.Status) $($sig.StatusMessage)"
}
if ($sig.Status -ne 'Valid') {
    Write-Host "Note: signature status is '$($sig.Status)' on this build machine (self-signed dev cert)." -ForegroundColor Yellow
    Write-Host 'The package is signed; the target PC trusts it via the bundled .cer during install.'
}

# Build a clean deploy folder. The private PFX remains outside this folder.
New-Item -ItemType Directory -Path $deploy -Force | Out-Null
Copy-Item $portable (Join-Path $deploy 'portable') -Recurse -Force
Copy-Item $msix, $cerPath, $thumbPath -Destination $deploy -Force
Copy-Item (Join-Path $root 'install-current-user.ps1'), (Join-Path $root 'uninstall-current-user.ps1') -Destination $deploy -Force
Copy-Item (Join-Path $root 'README.md') -Destination $deploy -Force

Write-Host ''
Write-Host 'Build completed.' -ForegroundColor Green
Write-Host "Deploy folder: $deploy"
Write-Host "Sparse identity package: $msix"
Write-Host 'Copy only out\deploy to a stable user-writable location on the target PC.'
Write-Host 'The private signing PFX remains in out and is not copied into deploy.'
