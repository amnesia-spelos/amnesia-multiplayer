<#
.SYNOPSIS
Creates the forwarding VPS (and its free SSH key and firewall group, if missing).

.PARAMETER JoinerIp
Public IPv4 addresses allowed to reach the Multiplayer Relay port. Defaults to this PC's public IP.

.PARAMETER Plan
vc2-1c-1gb is $5/mo billed hourly. The listed free plan (vc2-1c-0.5gb-free) was refused for this
account in fra ("plan is not available in the selected region").

.PARAMETER PrepareOnly
Only ensure the SSH key and firewall group exist; create no instance (costs nothing).
#>
param(
    [string[]]$JoinerIp,
    [string]$Region = 'fra',
    [string]$Plan = 'vc2-1c-1gb',
    [int]$OsId = 2625, # Debian 13 x64 (trixie)
    [switch]$PrepareOnly
)
. (Join-Path $PSScriptRoot 'common.ps1')

$hostIp = Get-PublicIp
if (-not $JoinerIp) { $JoinerIp = @($hostIp) }

# SSH key: the private half lives in the Bitwarden agent; only the public key is uploaded.
if (-not (Test-Path $PublicKeyPath)) { throw "Public key not found at $PublicKeyPath." }
$publicKey = (Get-Content $PublicKeyPath -Raw).Trim()
$sshKey = (Invoke-Vultr ssh-key list).ssh_keys | Where-Object name -eq $VpsName | Select-Object -First 1
if (-not $sshKey) {
    $sshKey = (Invoke-Vultr ssh-key create --name $VpsName --key $publicKey).ssh_key
    Write-Host "Uploaded SSH key $($sshKey.id)."
}

# Firewall group: SSH from the Session Host only, the relay port from each Joining Player.
$group = Get-VpsFirewallGroup
if (-not $group) {
    $group = (Invoke-Vultr firewall group create --description $VpsName).firewall_group
    Write-Host "Created firewall group $($group.id)."
}
$rules = @((Invoke-Vultr firewall rule list $group.id).firewall_rules)
$wanted = @(@{ Port = '22'; Ip = $hostIp; Note = 'Session Host SSH' }) +
    @($JoinerIp | ForEach-Object { @{ Port = "$RelayPort"; Ip = $_; Note = 'Joining Player relay' } })
foreach ($rule in $wanted) {
    if ($rules | Where-Object { $_.port -eq $rule.Port -and $_.subnet -eq $rule.Ip -and $_.subnet_size -eq 32 }) { continue }
    Invoke-Vultr firewall rule create $group.id -t v4 -p tcp -r $rule.Port -s $rule.Ip -z 32 -n $rule.Note | Out-Null
    Write-Host "Allowed TCP $($rule.Port) from $($rule.Ip)."
}

if ($PrepareOnly) { Write-Host 'Prepared; no instance created.'; return }

$instance = Get-VpsInstance
if ($instance) { Write-Host "Instance already exists at $($instance.main_ip)."; return }

$instance = (Invoke-Vultr instance create -r $Region -p $Plan --os $OsId -l $VpsName --host $VpsName `
    -s $sshKey.id --firewall-group $group.id --userdata-file (Join-Path $PSScriptRoot 'cloud-init.yaml')).instance
Write-Host "Creating instance $($instance.id) ($Plan in $Region)..."

$deadline = (Get-Date).AddMinutes(10)
do {
    Start-Sleep -Seconds 10
    $instance = (Invoke-Vultr instance get $instance.id).instance
    Write-Host "  status=$($instance.status) power=$($instance.power_status) server=$($instance.server_status)"
} until (($instance.status -eq 'active' -and $instance.server_status -eq 'ok') -or (Get-Date) -gt $deadline)

if ($instance.status -ne 'active') { throw 'Instance did not become active within 10 minutes.' }
Write-Host "VPS ready at $($instance.main_ip). cloud-init may need another minute before the tunnel works."
Write-Host "Joining Player: /join $($instance.main_ip)"
