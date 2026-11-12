# Signs the Work IQ service account in by device code and stores the refresh token on the VM,
# DPAPI-encrypted with the VM's machine key. The token is never printed.
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (Test-Path "$PSScriptRoot\deployment.local.ps1") { . "$PSScriptRoot\deployment.local.ps1" }

$tenant = $env:RTBOT_TENANT_ID
$client = $env:RTBOT_APP_ID
if (-not $tenant -or -not $client) { throw 'Set RTBOT_TENANT_ID and RTBOT_APP_ID (see scripts/deployment.example.ps1).' }
$scope = 'api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask offline_access'

$dc = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenant/oauth2/v2.0/devicecode" -Body @{ client_id = $client; scope = $scope }
Write-Host $dc.message -ForegroundColor Yellow

$deadline = (Get-Date).AddSeconds($dc.expires_in)
$tokens = $null
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $dc.interval
    try {
        $tokens = Invoke-RestMethod -Method Post -Uri "https://login.microsoftonline.com/$tenant/oauth2/v2.0/token" -Body @{
            grant_type = 'urn:ietf:params:oauth:grant-type:device_code'; client_id = $client; device_code = $dc.device_code }
        break
    }
    catch {
        $d = $_.ErrorDetails.Message | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($d.error -in 'authorization_pending', 'slow_down') { continue }
        throw "Device code flow failed: $($d.error)"
    }
}
if (-not $tokens.refresh_token) { throw 'No refresh token returned.' }
Write-Host 'Signed in. Pushing encrypted token to the VM...' -ForegroundColor Cyan

$b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($tokens.refresh_token))
$remote = @"
Add-Type -AssemblyName System.Security
`$plain = [Convert]::FromBase64String('$b64')
`$entropy = [Text.Encoding]::UTF8.GetBytes('RtBot.WorkIq.RefreshToken.v1')
`$cipher = [Security.Cryptography.ProtectedData]::Protect(`$plain, `$entropy, [Security.Cryptography.DataProtectionScope]::LocalMachine)
[IO.File]::WriteAllBytes('C:\rtbot\state\workiq.token', `$cipher)
Restart-Service RtBot -Force
Start-Sleep 20
'Service: ' + (Get-Service RtBot).Status
"@
$tmp = Join-Path $env:TEMP ('wq-' + [Guid]::NewGuid() + '.ps1')
try {
    $remote | Set-Content $tmp -Encoding UTF8
    az vm run-command invoke -g $env:RTBOT_RESOURCE_GROUP -n $env:RTBOT_VM_NAME --command-id RunPowerShellScript --scripts "@$tmp" -o json |
        ConvertFrom-Json | Select-Object -ExpandProperty value | Select-Object -ExpandProperty message
}
finally {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
}
