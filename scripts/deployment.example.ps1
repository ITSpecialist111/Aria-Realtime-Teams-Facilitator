# Copy to deployment.local.ps1 (git-ignored) and fill in. Scripts dot-source it when present.
$env:RTBOT_TENANT_ID      = '00000000-0000-0000-0000-000000000000'
$env:RTBOT_APP_ID         = '00000000-0000-0000-0000-000000000000'   # Entra app id of the calling bot
$env:RTBOT_HOST           = 'your-bot.region.cloudapp.azure.com'      # public FQDN of the media VM
$env:RTBOT_RESOURCE_GROUP = 'rg-rtbot'
$env:RTBOT_VM_NAME        = 'vm-rtbot'
