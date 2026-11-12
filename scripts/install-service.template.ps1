$ErrorActionPreference = 'Stop'

$installRoot = 'C:\rtbot'

# The client secret is NOT held in a machine environment variable: that registry key is
# readable by BUILTIN\Users. It goes into a DPAPI-protected file instead.
$settings = @{
    'DOTNET_ENVIRONMENT'                                        = 'Production'
    'OTEL_SERVICE_NAME'                                         = 'rtbot-aria'
    'OTEL_RESOURCE_ATTRIBUTES'                                  = 'service.namespace=teams-agents,deployment.environment=pov'
    'RtBot__AzureAd__TenantId'                                  = '__TENANT_ID__'
    'RtBot__AzureAd__AppId'                                     = '__APP_ID__'
    'RtBot__AzureAd__AppSecret'                                 = ''
    'RtBot__AzureAd__AppSecretFile'                             = 'C:\rtbot\state\client.secret'
    'RtBot__Media__ServiceFqdn'                                 = '__FQDN__'
    'RtBot__Media__CertificateThumbprint'                       = '__THUMBPRINT__'
    'RtBot__Media__SignalingInternalPort'                       = '443'
    'RtBot__Media__SignalingPublicPort'                         = '443'
    'RtBot__Media__MediaInternalPort'                           = '8445'
    'RtBot__Media__MediaPublicPort'                             = '8445'
    'RtBot__Media__InstanceOrdinal'                             = '0'
    'RtBot__Voice__Endpoint'                                    = '__AI_ENDPOINT__'
    'RtBot__Voice__Model'                                       = 'gpt-realtime'
    'RtBot__Voice__VoiceName'                                   = 'en-GB-SoniaNeural'
    'RtBot__Voice__TranscriptionModel'                          = 'whisper-1'
    'RtBot__Voice__Language'                                    = 'en'
    'RtBot__Voice__AgentName'                                   = 'Aria'
    'RtBot__Tools__TokenCachePath'                              = 'C:\rtbot\state\workiq.token'
    'RtBot__ManagedIdentityClientId'                            = '__MI_CLIENT_ID__'
    'RtBot__Observability__ApplicationInsightsConnectionString' = '__APPI__'
}

foreach ($key in $settings.Keys) {
    [Environment]::SetEnvironmentVariable($key, $settings[$key], 'Machine')
}

New-Item -ItemType Directory -Force -Path "$installRoot\state" | Out-Null

Add-Type -AssemblyName System.Security
$entropy = [System.Text.Encoding]::UTF8.GetBytes('RtBot.Secret.v1')
$cipher = [System.Security.Cryptography.ProtectedData]::Protect(
    [System.Text.Encoding]::UTF8.GetBytes('__APP_SECRET__'),
    $entropy,
    [System.Security.Cryptography.DataProtectionScope]::LocalMachine)
[System.IO.File]::WriteAllBytes("$installRoot\state\client.secret", $cipher)

Stop-Service RtBot -ErrorAction SilentlyContinue

# The media platform binds TCP 8445 natively; starting again before the old process has
# released it fails initialisation and takes the host down with it.
$deadline = (Get-Date).AddSeconds(45)
while ((Get-Date) -lt $deadline) {
    $held = Get-NetTCPConnection -State Listen -LocalPort 8445 -ErrorAction SilentlyContinue
    if (-not $held -and -not (Get-Process RtBot -ErrorAction SilentlyContinue)) { break }
    Start-Sleep -Seconds 2
}

New-Item -ItemType Directory -Force -Path "$installRoot\app" | Out-Null
Get-ChildItem "$installRoot\app" -Recurse | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$installRoot\stage\*" "$installRoot\app" -Recurse -Force

if (-not (Get-Service RtBot -ErrorAction SilentlyContinue)) {
    New-Service -Name RtBot -DisplayName 'RT Bot - Teams Realtime Voice Agent' `
        -BinaryPathName "`"$installRoot\app\RtBot.exe`"" -StartupType Automatic | Out-Null

    # A dropped meeting is worse than a restart; recover automatically.
    sc.exe failure RtBot reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
}

Start-Service RtBot -ErrorAction SilentlyContinue
Start-Sleep -Seconds 15

Write-Output "Service: $((Get-Service RtBot).Status)"
Write-Output '--- listening ---'
Get-NetTCPConnection -State Listen -LocalPort 443, 8445 -ErrorAction SilentlyContinue |
    Select-Object LocalAddress, LocalPort | Format-Table | Out-String

Write-Output '--- health ---'
try {
    Write-Output ((Invoke-RestMethod -Uri 'https://__FQDN__/api/health' -TimeoutSec 25) | ConvertTo-Json -Compress)
}
catch {
    Write-Output "health probe failed: $($_.Exception.Message)"
}

