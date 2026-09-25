<#
.SYNOPSIS
Run on the Session Host's PC after /host: exposes the local Multiplayer Relay on the VPS's public IP.
Keep the window open for the whole Multiplayer Session; Ctrl+C closes the tunnel.
#>
. (Join-Path $PSScriptRoot 'common.ps1')

$instance = Get-VpsInstance
if (-not $instance) { throw 'No VPS exists; run up.ps1 first.' }
$ip = $instance.main_ip

Write-Host "Forwarding ${ip}:$RelayPort -> 127.0.0.1:$RelayPort. Joining Player: /join $ip"
# -i with the public key plus IdentitiesOnly selects the Bitwarden key without offering every other key.
& $WindowsSsh -N `
    -i $PublicKeyPath -o IdentitiesOnly=yes `
    -o StrictHostKeyChecking=accept-new `
    -o ExitOnForwardFailure=yes `
    -o ServerAliveInterval=15 -o ServerAliveCountMax=3 `
    -R "0.0.0.0:${RelayPort}:127.0.0.1:$RelayPort" "root@$ip"
