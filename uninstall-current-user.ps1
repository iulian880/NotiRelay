$ErrorActionPreference = 'Stop'
$packageName = 'com.iulian.NotiRelay'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$thumbPath = Join-Path $root 'NotiRelay.thumbprint.txt'

$package = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($package) {
    Remove-AppxPackage -Package $package.PackageFullName
    Write-Host 'Sparse package registration removed for the current user.' -ForegroundColor Green
}
else {
    Write-Host 'No registration found for the current user.'
}

if (Test-Path $thumbPath) {
    $thumb = (Get-Content $thumbPath -Raw).Trim()
    if ($thumb) {
        $cert = Get-Item "Cert:\CurrentUser\TrustedPeople\$thumb" -ErrorAction SilentlyContinue
        if ($cert) {
            Remove-Item $cert.PSPath -Confirm:$false
            Write-Host 'Development signing certificate removed from CurrentUser\TrustedPeople.'
        }
    }
}
Write-Host 'You may now move or delete the deployment folder.'
