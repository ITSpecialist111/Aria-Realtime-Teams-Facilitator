$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if (Test-Path "$PSScriptRoot\deployment.local.ps1") { . "$PSScriptRoot\deployment.local.ps1" }
$rg = $env:RTBOT_RESOURCE_GROUP
$vm = $env:RTBOT_VM_NAME
if (-not $rg -or -not $vm) { throw 'Set RTBOT_RESOURCE_GROUP and RTBOT_VM_NAME (see scripts/deployment.example.ps1).' }

$stage = 'artifacts/src-stage'
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path "$stage/RtBot" | Out-Null

$src = (Resolve-Path src/RtBot).Path
Get-ChildItem $src -Recurse -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
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

Write-Host '==> restarting service' -ForegroundColor Cyan
az vm run-command invoke -g $rg -n $vm --command-id RunPowerShellScript `
    --scripts '@artifacts/restart.ps1' -o json |
    ConvertFrom-Json | Select-Object -ExpandProperty value |
    Select-Object -ExpandProperty message
