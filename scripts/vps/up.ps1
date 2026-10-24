<#
.SYNOPSIS
Creates the forwarding VPS (and its free SSH key and firewall group, if missing).

Who may reach the Multiplayer Relay port must be chosen explicitly, before the session:
-JoinerIp (allowlist) or -OpenRelay (anyone, until down.ps1). Forgetting this was the one
snag of the first run: the firewall silently drops the join, which times out.

.PARAMETER JoinerIp
Public IPv4 addresses allowed to reach the Multiplayer Relay port. The Joining Player gets theirs
with `curl.exe -s https://api.ipify.org`. `self` means this PC's public IP (both PCs on one LAN).
Allowed IPs stay in the firewall group across VPSes.

.PARAMETER OpenRelay
Allow the Multiplayer Relay port from any IPv4 address until down.ps1 removes the rule. No IP needs
to be known, but the unauthenticated relay is then public: scanners can take the one join slot.

.PARAMETER FriendHosts
The friend is the Session Host and this PC joins. Generates a throwaway SSH key that may only open
the relay's reverse forward, opens TCP 443 (the friend's SSH) to anyone until down.ps1, allows the
relay port from this PC, and writes out\multimnesia-tunnel(.zip) to send to the friend once the VPS
exists. No IP needs to be asked for.

.PARAMETER Plan
vc2-1c-1gb is $5/mo billed hourly. The listed free plan (vc2-1c-0.5gb-free) was refused for this
account in fra ("plan is not available in the selected region").

.PARAMETER PrepareOnly
Only ensure the SSH key and firewall group exist; create no instance (costs nothing).
#>
param(
    [string[]]$JoinerIp,
    [switch]$OpenRelay,
    [switch]$FriendHosts,
    [string]$Region = 'fra',
    [string]$Plan = 'vc2-1c-1gb',
    [int]$OsId = 2625, # Debian 13 x64 (trixie)
    [switch]$PrepareOnly
)
. (Join-Path $PSScriptRoot 'common.ps1')

$hostIp = Get-PublicIp
if ($FriendHosts) { $JoinerIp = @($JoinerIp) + 'self' }
$JoinerIp = @($JoinerIp | Where-Object { $_ } | ForEach-Object { if ($_ -eq 'self') { $hostIp } else { $_ } } | Select-Object -Unique)
if (-not $PrepareOnly -and -not $JoinerIp -and -not $OpenRelay -and -not (Get-VpsInstance)) {
    throw 'Choose who may join: -JoinerIp <their public IP> (they run: curl.exe -s https://api.ipify.org), -JoinerIp self, or -OpenRelay.'
}

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
$wanted = @(@{ Port = '22'; Ip = $hostIp; Size = 32; Note = 'Session Host SSH' }) +
    @($JoinerIp | ForEach-Object { @{ Port = "$RelayPort"; Ip = $_; Size = 32; Note = 'Joining Player relay' } })
if ($OpenRelay) { $wanted += @{ Port = "$RelayPort"; Ip = '0.0.0.0'; Size = 0; Note = $OpenRelayNote } }
if ($FriendHosts) { $wanted += @{ Port = "$FriendSshPort"; Ip = '0.0.0.0'; Size = 0; Note = $FriendSshNote } }
foreach ($rule in $wanted) {
    if ($rules | Where-Object { $_.port -eq $rule.Port -and $_.subnet -eq $rule.Ip -and $_.subnet_size -eq $rule.Size }) { continue }
    Invoke-Vultr firewall rule create $group.id -t v4 -p tcp -r $rule.Port -s $rule.Ip -z $rule.Size -n $rule.Note | Out-Null
    Write-Host "Allowed TCP $($rule.Port) from $($rule.Ip)/$($rule.Size)."
}

if ($PrepareOnly) { Write-Host 'Prepared; no instance created.'; return }

$instance = Get-VpsInstance
if ($instance) { Write-Host "Instance already exists at $($instance.main_ip)."; return }

# A fresh friend key per VPS: it only works on this instance, so down.ps1 revokes it.
$userData = Get-Content (Join-Path $PSScriptRoot 'cloud-init.yaml') -Raw
if ($FriendHosts) {
    $friendOut = Split-Path $FriendTunnelDir
    if (Test-Path $friendOut) { Remove-Item $friendOut -Recurse -Force }
    New-Item -ItemType Directory $FriendTunnelDir | Out-Null
    $friendKey = Join-Path $FriendTunnelDir 'tunnel_key'
    # Through cmd so the empty passphrase survives both Windows PowerShell and PowerShell 7 argument passing.
    cmd /c "`"$WindowsSshKeygen`" -q -t ed25519 -N `"`" -C multimnesia-tunnel -f `"$friendKey`""
    if ($LASTEXITCODE -ne 0) { throw 'ssh-keygen failed.' }
    $friendPublicKey = (Get-Content "$friendKey.pub" -Raw).Trim()
    Remove-Item "$friendKey.pub"
    $userData = $userData -replace '(?m)^#friend-host:', '' `
        -replace '\{\{JOINER_IP\}\}', $hostIp -replace '\{\{TUNNEL_PUBLIC_KEY\}\}', $friendPublicKey
}
$userDataFile = New-TemporaryFile
Set-Content $userDataFile $userData -NoNewline

try {
    $instance = (Invoke-Vultr instance create -r $Region -p $Plan --os $OsId -l $VpsName --host $VpsName `
        -s $sshKey.id --firewall-group $group.id --userdata-file $userDataFile).instance
}
finally { Remove-Item $userDataFile }
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

if ($FriendHosts) {
    $template = Get-Content (Join-Path $PSScriptRoot 'friend-tunnel.cmd') -Raw
    $script = $template -replace '\{\{VPS_IP\}\}', $instance.main_ip -replace '\{\{SSH_PORT\}\}', $FriendSshPort `
        -replace '\{\{RELAY_PORT\}\}', $RelayPort
    # cmd.exe needs CRLF line endings.
    Set-Content (Join-Path $FriendTunnelDir 'tunnel.cmd') ($script -replace '\r?\n', "`r`n") -NoNewline
    $zip = "$FriendTunnelDir.zip"
    Compress-Archive -Path $FriendTunnelDir -DestinationPath $zip -Force
    Write-Host "Send the friend $zip (it contains a key for this VPS only; down.ps1 revokes it)."
}
