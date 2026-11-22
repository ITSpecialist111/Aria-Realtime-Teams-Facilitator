$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (Test-Path "$PSScriptRoot\deployment.local.ps1") { . "$PSScriptRoot\deployment.local.ps1" }
$rg = $env:RTBOT_RESOURCE_GROUP
$vm = $env:RTBOT_VM_NAME
if (-not $rg -or -not $vm) { throw 'Set RTBOT_RESOURCE_GROUP and RTBOT_VM_NAME (see scripts/deployment.example.ps1).' }

if (Test-Path 'apps/aria-console/package.json') {
    Write-Host '==> building apps/aria-console host bundle' -ForegroundColor Cyan
    Push-Location 'apps/aria-console'
    try { npm run build:host | Out-Host } finally { Pop-Location }
}

$stage = 'artifacts/src-stage'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path "$stage/RtBot" | Out-Null

$src = (Resolve-Path src/RtBot).Path
Get-ChildItem $src -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|wwwroot)\\' } |
    ForEach-Object {
        $dest = Join-Path "$stage/RtBot" $_.FullName.Substring($src.Length + 1)
        New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
        Copy-Item $_.FullName $dest
    }

Compress-Archive -Path "$stage/RtBot" -DestinationPath 'artifacts/src.zip' -Force
$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes('artifacts/src.zip'))

$push = @"
`$ErrorActionPreference='Stop'
Get-ChildItem C:\rtbot\src -Recurse | Remove-Item -Recurse -Force -EA SilentlyContinue
[IO.File]::WriteAllBytes('C:\rtbot\src.zip', [Convert]::FromBase64String('$b64'))
Expand-Archive -Path C:\rtbot\src.zip -DestinationPath C:\rtbot\src -Force
Remove-Item C:\rtbot\src.zip -Force
Remove-Item C:\rtbot\logs\build.log -Force -EA SilentlyContinue
# Publish only copies files newer than the destination; a stale DLL from an older framework can
# carry a later timestamp than its replacement package and silently survive.
Get-ChildItem C:\rtbot\stage -EA SilentlyContinue | Remove-Item -Recurse -Force -EA SilentlyContinue
schtasks /Run /TN RtBotBuild | Out-Null
`$deadline = (Get-Date).AddMinutes(8)
while ((Get-Date) -lt `$deadline -and -not ((Test-Path C:\rtbot\logs\build.log) -and (Select-String -Path C:\rtbot\logs\build.log -Pattern 'EXITCODE=' -Quiet))) { Start-Sleep 5 }
Get-Content C:\rtbot\logs\build.log | Select-String 'error|EXITCODE' | Select-Object -Last 15
"@
$push | Set-Content artifacts/push-src.ps1 -Encoding UTF8

Write-Host '==> pushing source and building' -ForegroundColor Cyan
az vm run-command invoke -g $rg -n $vm --command-id RunPowerShellScript `
    --scripts '@artifacts/push-src.ps1' -o json |
    ConvertFrom-Json | Select-Object -ExpandProperty value |
    Select-Object -ExpandProperty message

if (Test-Path 'src/RtBot/wwwroot/console/index.html') {
    Write-Host '==> uploading compiled companion console assets' -ForegroundColor Cyan
    $hasVendorOnHost = (az vm run-command invoke -g $rg -n $vm --command-id RunPowerShellScript `
        --scripts "Write-Output (Test-Path 'C:\rtbot\app\wwwroot\console\assets\livekit-client*.js')" `
        --query "value[0].message" -o tsv) -match 'True'

    if ($hasVendorOnHost) {
        $deltaStage = 'artifacts/console-delta'
        Remove-Item $deltaStage,'artifacts/console-delta.zip' -Recurse -Force -EA SilentlyContinue
        $consoleRoot = (Resolve-Path 'src/RtBot/wwwroot/console').Path
        Get-ChildItem $consoleRoot -Recurse -File |
            Where-Object { $_.Name -notmatch '\.woff2$|^(react-vendor|livekit-client|cmr-sdk|gsap)-' } |
            ForEach-Object {
                $dst = Join-Path $deltaStage $_.FullName.Substring($consoleRoot.Length + 1)
                New-Item -ItemType Directory -Force -Path (Split-Path $dst) | Out-Null
                Copy-Item $_.FullName $dst
            }
        Compress-Archive -Path "$deltaStage/*" -DestinationPath 'artifacts/console-delta.zip' -Force
        $deltaB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes('artifacts/console-delta.zip'))
        $unpackDelta = @"
`$ErrorActionPreference='Stop'
New-Item -ItemType Directory -Force -Path 'C:\rtbot\stage\wwwroot\console' | Out-Null
Copy-Item 'C:\rtbot\app\wwwroot\console\*' 'C:\rtbot\stage\wwwroot\console' -Recurse -Force
[IO.File]::WriteAllBytes('C:\rtbot\delta.zip', [Convert]::FromBase64String('$deltaB64'))
Expand-Archive -Path 'C:\rtbot\delta.zip' -DestinationPath 'C:\rtbot\stage\wwwroot\console' -Force
Remove-Item 'C:\rtbot\delta.zip' -Force -EA SilentlyContinue
Write-Output 'CONSOLE_DELTA_OK'
"@
        $unpackDelta | Set-Content 'artifacts/unpack-console.ps1' -Encoding UTF8
        az vm run-command invoke -g $rg -n $vm --command-id RunPowerShellScript `
            --scripts '@artifacts/unpack-console.ps1' -o json |
            ConvertFrom-Json | Select-Object -ExpandProperty value |
            Select-Object -ExpandProperty message
    }
    else {
        Compress-Archive -Path 'src/RtBot/wwwroot/console/*' -DestinationPath 'artifacts/console.zip' -Force
        $consoleB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes('artifacts/console.zip'))
        $chunkSize = 180000
        for ($offset = 0; $offset -lt $consoleB64.Length; $offset += $chunkSize) {
            $len = [Math]::Min($chunkSize, $consoleB64.Length - $offset)
            $slice = $consoleB64.Substring($offset, $len)
            $mode = if ($offset -eq 0) { 'Set-Content' } else { 'Add-Content' }
            $chunkScript = "$mode -Path 'C:\rtbot\console.b64' -Value '$slice' -NoNewline"
            $chunkScript | Set-Content 'artifacts/push-console-chunk.ps1' -Encoding UTF8
            az vm run-command invoke -g $rg -n $vm --command-id RunPowerShellScript `
                --scripts '@artifacts/push-console-chunk.ps1' -o none
        }
        $unpackConsole = @"
`$ErrorActionPreference='Stop'
[IO.File]::WriteAllBytes('C:\rtbot\console.zip', [Convert]::FromBase64String([IO.File]::ReadAllText('C:\rtbot\console.b64')))
New-Item -ItemType Directory -Force -Path 'C:\rtbot\stage\wwwroot\console' | Out-Null
Expand-Archive -Path 'C:\rtbot\console.zip' -DestinationPath 'C:\rtbot\stage\wwwroot\console' -Force
Remove-Item 'C:\rtbot\console.b64','C:\rtbot\console.zip' -Force -EA SilentlyContinue
Write-Output 'CONSOLE_ASSETS_OK'
"@
        $unpackConsole | Set-Content 'artifacts/unpack-console.ps1' -Encoding UTF8
        az vm run-command invoke -g $rg -n $vm --command-id RunPowerShellScript `
            --scripts '@artifacts/unpack-console.ps1' -o json |
            ConvertFrom-Json | Select-Object -ExpandProperty value |
            Select-Object -ExpandProperty message
    }
}

Write-Host '==> restarting service' -ForegroundColor Cyan
az vm run-command invoke -g $rg -n $vm --command-id RunPowerShellScript `
    --scripts '@artifacts/restart.ps1' -o json |
    ConvertFrom-Json | Select-Object -ExpandProperty value |
    Select-Object -ExpandProperty message
