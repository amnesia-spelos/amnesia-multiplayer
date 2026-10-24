<#
.SYNOPSIS
Destroys the forwarding VPS to stop billing, closes -OpenRelay and -FriendHosts rules, and
deletes the friend's tunnel folder (its key dies with the VPS anyway).
The SSH key, firewall group, and allowlisted IPs are free and are kept.
#>
. (Join-Path $PSScriptRoot 'common.ps1')

$group = Get-VpsFirewallGroup
if ($group) {
    # Highest rule number first, so deleting one does not renumber the next.
    $open = @((Invoke-Vultr firewall rule list $group.id).firewall_rules) |
        Where-Object notes -in $OpenRelayNote, $FriendSshNote | Sort-Object id -Descending
    foreach ($rule in $open) {
        Invoke-Vultr firewall rule delete $group.id $rule.id | Out-Null
        Write-Host "Closed the session-only rule $($rule.id) ($($rule.notes))."
    }
}

$friendOut = Split-Path $FriendTunnelDir
if (Test-Path $friendOut) {
    Remove-Item $friendOut -Recurse -Force
    Write-Host "Deleted the friend's tunnel folder."
}

$instance = Get-VpsInstance
if (-not $instance) { Write-Host 'No VPS exists.'; return }
Invoke-Vultr instance delete $instance.id | Out-Null
Write-Host "Deleted instance $($instance.id) ($($instance.main_ip))."
