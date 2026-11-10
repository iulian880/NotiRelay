$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$portable = Join-Path $root 'portable'
$certPath = Join-Path $root 'NotiRelay.cer'
$msix = Join-Path $root 'NotiRelay.identity.msix'
$packageName = 'com.iulian.NotiRelay'

foreach ($path in @($portable, $certPath, $msix)) {
    if (-not (Test-Path $path)) { throw "Required deployment file is missing: $path" }
}
if (-not (Test-Path (Join-Path $portable 'NotiRelay.exe'))) {
    throw 'NotiRelay.exe is missing from the portable folder.'
}

Write-Host 'This registers the sparse package for the current Windows user only.' -ForegroundColor Cyan
Write-Host 'The executable remains in the portable folder; no machine-wide install or admin elevation is requested.'
Write-Host 'Continue only if your organization has approved this application and the ntfy destination.'
$answer = Read-Host 'Type YES to register this package for your current user'
if ($answer -cne 'YES') { throw 'Registration cancelled.' }

$existing = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if ($existing) {
    throw 'An earlier version is registered. Run uninstall-current-user.ps1 first, then run this installer again.'
}

Import-Certificate -FilePath $certPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
Add-AppxPackage -Path $msix -ExternalLocation $portable

$installed = Get-AppxPackage -Name $packageName -ErrorAction SilentlyContinue
if (-not $installed) {
    throw 'Windows did not report the package as registered after installation.'
}
Write-Host 'Package identity registered for the current user.' -ForegroundColor Green
Write-Host 'The app binaries remain in the portable folder. Do not move or rename the deployment folder while registered.'
Write-Host 'If Windows or company policy blocks this operation, stop and contact IT; do not try to disable the policy.'
