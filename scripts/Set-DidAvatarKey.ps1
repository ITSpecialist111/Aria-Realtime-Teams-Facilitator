<#
.SYNOPSIS
    Encrypts a D-ID Basic API key (`username:password`) into DPAPI on vm-rtbot or the local Windows host.
    Secrets are read interactively with -MaskInput and never written to git or plain-text files.
#>
[CmdletBinding()]
param(
    [string]$AgentId = "",
    [switch]$LocalOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$localConfig = Join-Path $PSScriptRoot "deployment.local.ps1"
if (Test-Path $localConfig) {
    . $localConfig
}

$secure = Read-Host -Prompt "Paste your D-ID API Key (username:password)" -AsSecureString
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
try {
    $rawKey = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr)
} finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}

if ([string]::IsNullOrWhiteSpace($rawKey)) {
    throw "No D-ID API key entered."
}

if ($LocalOnly) {
    Add-Type -AssemblyName System.Security
    $dir = "C:\rtbot\state"
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
    $entropy = [Text.Encoding]::UTF8.GetBytes("RtBot.Secret.v1")
    $cipher = [Security.Cryptography.ProtectedData]::Protect(
        [Text.Encoding]::UTF8.GetBytes($rawKey.Trim()),
        $entropy,
        [Security.Cryptography.DataProtectionScope]::LocalMachine)
    [IO.File]::WriteAllBytes((Join-Path $dir "did.secret"), $cipher)
    if ($AgentId) {
        Set-Content -Path (Join-Path $dir "did-agent.id") -Value $AgentId.Trim() -NoNewline
    }
    Write-Host "Saved DPAPI-encrypted D-ID secret to C:\rtbot\state\did.secret." -ForegroundColor Green
    return
}

if (-not $env:RTBOT_HOST -or -not $env:RTBOT_APP_ID) {
    throw "Set RTBOT_HOST and RTBOT_APP_ID in scripts/deployment.local.ps1 first, or pass -LocalOnly."
}

$token = az account get-access-token --resource "api://$($env:RTBOT_APP_ID)" --query accessToken -o tsv
if (-not $token) {
    throw "Could not acquire Entra operator token via az account get-access-token. Run 'az login' first."
}

$payload = @{
    apiKey  = $rawKey.Trim()
    agentId = if ([string]::IsNullOrWhiteSpace($AgentId)) { $null } else { $AgentId.Trim() }
} | ConvertTo-Json -Compress

$resp = Invoke-RestMethod `
    -Method Post `
    -Uri "https://$($env:RTBOT_HOST)/api/avatar/configure" `
    -Headers @{ Authorization = "Bearer $token" } `
    -ContentType "application/json" `
    -Body $payload

$rawKey = $null
Write-Host ("D-ID secret encrypted via DPAPI on $($env:RTBOT_HOST) (voice: $($resp.voiceId)).") -ForegroundColor Green
