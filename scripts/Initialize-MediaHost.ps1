<#
.SYNOPSIS
    Prepares the Windows media host: runtime, certificate, firewall and service account.

.DESCRIPTION
    Run once on the VM as Administrator. The Teams media platform will not start without a
    public-CA certificate in LocalMachine\My whose subject matches the FQDN it advertises,
    so this obtains one from Let's Encrypt over the HTTP-01 challenge.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ServiceFqdn,
    [Parameter(Mandatory)] [string] $ContactEmail,
    [string] $InstallRoot = 'C:\rtbot',
    [int]    $SignallingPort = 443,
    [int]    $MediaPort = 8445
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step { param([string] $Message) Write-Host "==> $Message" -ForegroundColor Cyan }

# ---------------------------------------------------------------------------
Write-Step 'Creating directories'
foreach ($dir in @($InstallRoot, "$InstallRoot\app", "$InstallRoot\logs", "$InstallRoot\state", "$InstallRoot\tools")) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

# ---------------------------------------------------------------------------
Write-Step 'Installing the Visual C++ runtime (required by the native media stack)'
$vcRedist = "$InstallRoot\tools\vc_redist.x64.exe"
if (-not (Test-Path $vcRedist)) {
    Invoke-WebRequest -Uri 'https://aka.ms/vs/17/release/vc_redist.x64.exe' -OutFile $vcRedist -UseBasicParsing
}
Start-Process -FilePath $vcRedist -ArgumentList '/install', '/quiet', '/norestart' -Wait -NoNewWindow

# ---------------------------------------------------------------------------
Write-Step 'Installing the ASP.NET Core 8 hosting bundle'
$dotnetInstaller = "$InstallRoot\tools\dotnet-install.ps1"
Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $dotnetInstaller -UseBasicParsing
& $dotnetInstaller -Channel '8.0' -Runtime 'aspnetcore' -InstallDir 'C:\Program Files\dotnet'

$machinePath = [Environment]::GetEnvironmentVariable('Path', 'Machine')
if ($machinePath -notlike '*C:\Program Files\dotnet*') {
    [Environment]::SetEnvironmentVariable('Path', "$machinePath;C:\Program Files\dotnet", 'Machine')
}

# ---------------------------------------------------------------------------
Write-Step 'Opening the Windows firewall'
$rules = @(
    @{ Name = 'RtBot-Signalling'; Port = $SignallingPort },
    @{ Name = 'RtBot-Media';      Port = $MediaPort },
    @{ Name = 'RtBot-Acme';       Port = 80 }
)
foreach ($rule in $rules) {
    Remove-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue
    New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Protocol TCP `
        -LocalPort $rule.Port -Action Allow | Out-Null
}

# ---------------------------------------------------------------------------
Write-Step "Requesting a Let's Encrypt certificate for $ServiceFqdn"

# The platform validates the certificate chain during MTLS with the Teams calling service,
# so a self-signed certificate will fail at MediaPlatform initialisation, not at request time.
$wacsDir = "$InstallRoot\tools\wacs"
if (-not (Test-Path "$wacsDir\wacs.exe")) {
    $zip = "$InstallRoot\tools\wacs.zip"
    Invoke-WebRequest -UseBasicParsing -OutFile $zip `
        -Uri 'https://github.com/win-acme/win-acme/releases/download/v2.2.9.1701/win-acme.v2.2.9.1701.x64.pluggable.zip'
    Expand-Archive -Path $zip -DestinationPath $wacsDir -Force
}

# Port 80 must be free for the HTTP-01 challenge; the bot listens on 443 only.
& "$wacsDir\wacs.exe" --target manual --host $ServiceFqdn --validation selfhosting `
    --store certificatestore --certificatestore My --accepttos --emailaddress $ContactEmail `
    --notaskscheduler:false --closeonfinish

$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -like "*$ServiceFqdn*" -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    throw "No certificate for $ServiceFqdn was found in LocalMachine\My after the ACME run."
}

Write-Host "Certificate thumbprint: $($cert.Thumbprint)" -ForegroundColor Green
Write-Host "Certificate expires:    $($cert.NotAfter)" -ForegroundColor Green

# ---------------------------------------------------------------------------
Write-Step 'Granting the network service read access to the private key'
$keyPath = (Get-ChildItem Cert:\LocalMachine\My\$($cert.Thumbprint)).PrivateKey
if ($keyPath) {
    $keyFile = Join-Path $env:ProgramData `
        "Microsoft\Crypto\RSA\MachineKeys\$($keyPath.CspKeyContainerInfo.UniqueKeyContainerName)"
    if (Test-Path $keyFile) {
        $acl = Get-Acl $keyFile
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            'NETWORK SERVICE', 'Read', 'Allow')))
        Set-Acl -Path $keyFile -AclObject $acl
    }
}

# ---------------------------------------------------------------------------
Write-Step 'Locking down the state directory'
# The delegated refresh token lives here, DPAPI-encrypted. Remove inherited user access.
$stateAcl = Get-Acl "$InstallRoot\state"
$stateAcl.SetAccessRuleProtection($true, $false)
foreach ($principal in 'SYSTEM', 'Administrators', 'NETWORK SERVICE') {
    $stateAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
        $principal, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')))
}
Set-Acl -Path "$InstallRoot\state" -AclObject $stateAcl

Write-Host ''
Write-Host 'Host prepared.' -ForegroundColor Green
Write-Host "  Set RtBot:Media:CertificateThumbprint to $($cert.Thumbprint)"
Write-Host "  Set RtBot:Media:ServiceFqdn           to $ServiceFqdn"
