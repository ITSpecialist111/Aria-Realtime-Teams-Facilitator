<#
.SYNOPSIS
    Brings Aria into a Microsoft Teams meeting.

.DESCRIPTION
    Uses your existing `az login` — no secrets, no app registration work. The Azure CLI is
    pre-authorised for the agent's Meetings.Join scope, so the token it already holds is enough.

.EXAMPLE
    ./Invite-Aria.ps1 -JoinUrl "https://teams.microsoft.com/l/meetup-join/..."

.EXAMPLE
    ./Invite-Aria.ps1 -List
    ./Invite-Aria.ps1 -Leave 3d005580-fae9-4439-93c5-90c0b492fc27
    ./Invite-Aria.ps1 -Say "Good morning everyone" -CallId 3d005580-...
#>
[CmdletBinding(DefaultParameterSetName = 'Join')]
param(
    [Parameter(ParameterSetName = 'Join', Position = 0)]
    [string] $JoinUrl,

    [Parameter(ParameterSetName = 'List')]
    [switch] $List,

    [Parameter(ParameterSetName = 'Leave')]
    [string] $Leave,

    [Parameter(ParameterSetName = 'Say', Mandatory)]
    [string] $Say,

    [Parameter(ParameterSetName = 'Say')]
    [string] $CallId,

    [Parameter(ParameterSetName = 'Status')]
    [switch] $Status,

    [Parameter(ParameterSetName = 'EnsureOnline')]
    [switch] $EnsureOnline,

    [string] $BotHost,
    [string] $ApiAppId,
    [string] $ResourceGroup,
    [string] $VmName,
    [switch] $SkipHostCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (Test-Path "$PSScriptRoot\deployment.local.ps1") { . "$PSScriptRoot\deployment.local.ps1" }
if (-not $BotHost) { $BotHost = $env:RTBOT_HOST }
if (-not $ApiAppId) { $ApiAppId = $env:RTBOT_APP_ID }
if (-not $ResourceGroup) { $ResourceGroup = $env:RTBOT_RESOURCE_GROUP }
if (-not $VmName) { $VmName = $env:RTBOT_VM_NAME }
if (-not $BotHost -or -not $ApiAppId) { throw 'Set RTBOT_HOST and RTBOT_APP_ID (see scripts/deployment.example.ps1).' }

function Assert-BotOnline {
    <#
        Tenant automation deallocates the VM overnight. A stopped host is indistinguishable
        from a broken bot at the Teams end — the call just goes to voicemail — so it is
        checked and started here rather than left to fail silently.
    #>
    if ($SkipHostCheck) { return }

    try {
        Invoke-RestMethod -Uri "https://$BotHost/api/health" -TimeoutSec 10 | Out-Null
        return
    }
    catch {
        Write-Host 'Agent host is not responding; checking the VM...' -ForegroundColor Yellow
    }

    $power = az vm get-instance-view -g $ResourceGroup -n $VmName `
        --query "instanceView.statuses[1].displayStatus" -o tsv 2>$null

    if ($power -ne 'VM running') {
        Write-Host "VM is '$power'. Starting it (this takes about a minute)..." -ForegroundColor Yellow
        az vm start -g $ResourceGroup -n $VmName -o none
    }

    $deadline = (Get-Date).AddMinutes(4)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 10
        try {
            Invoke-RestMethod -Uri "https://$BotHost/api/health" -TimeoutSec 10 | Out-Null
            Write-Host 'Agent host is online.' -ForegroundColor Green
            return
        }
        catch { }
    }

    throw "The agent host did not come online. Check the RtBot service on $VmName."
}

function Get-BotToken {
    $token = az account get-access-token --resource "api://$ApiAppId" --query accessToken -o tsv 2>&1
    if ($LASTEXITCODE -ne 0 -or -not $token -or $token.Length -lt 100) {
        throw "Could not get a token. Run 'az login --tenant $env:RTBOT_TENANT_ID' first.`n$token"
    }
    return $token
}

function Invoke-Bot {
    param([string] $Method, [string] $Path, $Body)

    Assert-BotOnline

    $params = @{
        Method  = $Method
        Uri     = "https://$BotHost$Path"
        Headers = @{ Authorization = "Bearer $(Get-BotToken)" }
        TimeoutSec = 90
    }
    if ($Body) {
        $params.Body = ($Body | ConvertTo-Json -Depth 5)
        $params.ContentType = 'application/json'
    }

    Invoke-RestMethod @params
}

switch ($PSCmdlet.ParameterSetName) {
    'EnsureOnline' {
        # Run this before calling Aria from the Teams address list: that path never touches
        # this script, so a stopped host would just ring out to voicemail.
        Assert-BotOnline
        Write-Host 'Aria is online and ready to take calls.' -ForegroundColor Green
    }

    'List' {
        $calls = Invoke-Bot GET '/api/meetings'
        if (-not $calls) { Write-Host 'Aria is not in any meetings.' -ForegroundColor Yellow; break }
        $calls | Format-Table callId, state, threadId -AutoSize
    }

    'Status' {
        Invoke-Bot GET '/api/diagnostics' | ConvertTo-Json -Depth 5
    }

    'Leave' {
        Invoke-Bot POST "/api/meetings/$Leave/leave" | Out-Null
        Write-Host 'Aria has left the meeting.' -ForegroundColor Green
    }

    'Say' {
        $id = $CallId
        if (-not $id) {
            $calls = @(Invoke-Bot GET '/api/meetings')
            if ($calls.Count -ne 1) { throw 'Specify -CallId: Aria is in zero or several meetings.' }
            $id = $calls[0].callId
        }
        Invoke-Bot POST "/api/meetings/$id/say" @{ text = $Say } | Out-Null
        Write-Host 'Sent.' -ForegroundColor Green
    }

    'Join' {
        if (-not $JoinUrl) {
            $JoinUrl = Read-Host 'Paste the Teams meeting join link'
        }

        Write-Host 'Bringing Aria into the meeting...' -ForegroundColor Cyan
        $result = Invoke-Bot POST '/api/meetings/join' @{ joinUrl = $JoinUrl }

        Write-Host ''
        Write-Host "Aria has joined. callId: $($result.callId)" -ForegroundColor Green
        Write-Host ''
        Write-Host 'She only replies when addressed by name. Try:' -ForegroundColor Gray
        Write-Host '  "Hey Aria, are you there?"' -ForegroundColor Gray
        Write-Host '  "Aria, what meetings do I have this week?"' -ForegroundColor Gray
        Write-Host ''
        Write-Host "To remove her:  ./Invite-Aria.ps1 -Leave $($result.callId)" -ForegroundColor Gray
    }
}
