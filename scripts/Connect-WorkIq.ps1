<#
.SYNOPSIS
    Signs a service account in once so the bot can call the delegated-only Work IQ MCP server.

.DESCRIPTION
    Work IQ does not support application-only authentication: every call runs as a signed-in
    user so Microsoft 365 permissions are enforced. A meeting bot has no interactive user, so a
    nominated service account consents here and the resulting refresh token is stored on the
    media host, DPAPI-encrypted with the machine key.

    Run this ON THE MEDIA HOST. The encrypted file cannot be decrypted anywhere else.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TenantId,
    [Parameter(Mandatory)] [string] $ClientId,
    [string] $Scope = 'api://workiq.svc.cloud.microsoft/WorkIQAgent.Ask',
    [string] $StatePath = 'C:\rtbot\state\workiq.token'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scopes = "$Scope offline_access"

Write-Host 'Requesting a device code...' -ForegroundColor Cyan
$deviceCode = Invoke-RestMethod -Method Post `
    -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/devicecode" `
    -Body @{ client_id = $ClientId; scope = $scopes }

Write-Host ''
Write-Host $deviceCode.message -ForegroundColor Yellow
Write-Host ''
Write-Host 'Sign in as the service account that the agent should act as in meetings.' -ForegroundColor Yellow
Write-Host ''

$deadline = (Get-Date).AddSeconds($deviceCode.expires_in)
$tokens = $null

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds $deviceCode.interval
    try {
        $tokens = Invoke-RestMethod -Method Post `
            -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
            -Body @{
                grant_type  = 'urn:ietf:params:oauth:grant-type:device_code'
                client_id   = $ClientId
                device_code = $deviceCode.device_code
            }
        break
    }
    catch {
        $detail = $_.ErrorDetails.Message | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($detail -and $detail.error -eq 'authorization_pending') { continue }
        if ($detail -and $detail.error -eq 'slow_down') { Start-Sleep -Seconds 5; continue }
        throw "Device code flow failed: $($detail.error) - $($detail.error_description)"
    }
}

if (-not $tokens) { throw 'Timed out waiting for sign-in.' }
if (-not $tokens.refresh_token) {
    throw 'No refresh token was returned. Ensure "offline_access" is consented for this app.'
}

Add-Type -AssemblyName System.Security

$directory = Split-Path -Parent $StatePath
if (-not (Test-Path $directory)) { New-Item -ItemType Directory -Force -Path $directory | Out-Null }

# Must match RtBot.Tools.ProtectedFileTokenStore.Entropy and its LocalMachine scope.
$entropy = [System.Text.Encoding]::UTF8.GetBytes('RtBot.WorkIq.RefreshToken.v1')
$plain = [System.Text.Encoding]::UTF8.GetBytes($tokens.refresh_token)
$cipher = [System.Security.Cryptography.ProtectedData]::Protect(
    $plain, $entropy, [System.Security.Cryptography.DataProtectionScope]::LocalMachine)

[System.IO.File]::WriteAllBytes($StatePath, $cipher)

Write-Host ''
Write-Host "Delegated refresh token stored (encrypted) at $StatePath" -ForegroundColor Green
Write-Host 'Restart the RtBot service to pick it up.' -ForegroundColor Green
