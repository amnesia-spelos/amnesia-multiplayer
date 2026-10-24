# Shared helpers for the VPS forwarding experiment. Dot-source; do not run directly.
$ErrorActionPreference = 'Stop'

$VpsName = 'multimnesia-vps'
$RelayPort = 5000
# Marks the up.ps1 -OpenRelay rule so down.ps1 can remove it.
$OpenRelayNote = 'Open relay until down.ps1'
# Marks the up.ps1 -FriendHosts rule that opens the friend's SSH port; down.ps1 removes it too.
$FriendSshNote = 'Friend SSH until down.ps1'
$FriendSshPort = 443
$RepoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
# up.ps1 -FriendHosts writes the folder (and zip) to send to the friend here; down.ps1 deletes it.
$FriendTunnelDir = Join-Path $PSScriptRoot 'out\multimnesia-tunnel'
$VultrCli = if ($env:VULTR_CLI) { $env:VULTR_CLI } else { Join-Path $RepoRoot '..\vultr-cli.exe' }
$PublicKeyPath = Join-Path $HOME '.ssh\multimnesia_vps.pub'
# Windows OpenSSH, not Git Bash's ssh: only it reads the Bitwarden agent's named pipe.
$WindowsSsh = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh.exe'
$WindowsSshKeygen = Join-Path $env:SystemRoot 'System32\OpenSSH\ssh-keygen.exe'

if (-not $env:VULTR_API_KEY) { throw 'VULTR_API_KEY is not set.' }
if (-not (Test-Path $VultrCli)) { throw "vultr-cli not found at $VultrCli; set VULTR_CLI." }

# Runs vultr-cli with JSON output; stderr (incl. the missing-config warning) surfaces only on failure.
function Invoke-Vultr {
    $errorFile = New-TemporaryFile
    try {
        $output = & $VultrCli @args -o json 2>$errorFile
        if ($LASTEXITCODE -ne 0) {
            $message = (Get-Content $errorFile | Where-Object { $_ -notmatch 'config file' }) -join ' '
            throw "vultr-cli $($args[0..1] -join ' ') failed: $message"
        }
    }
    finally { Remove-Item $errorFile -ErrorAction SilentlyContinue }
    if ($output) { ($output -join "`n") | ConvertFrom-Json }
}

function Get-VpsInstance {
    (Invoke-Vultr instance list).instances | Where-Object label -eq $VpsName | Select-Object -First 1
}

function Get-VpsFirewallGroup {
    (Invoke-Vultr firewall group list).firewall_groups | Where-Object description -eq $VpsName | Select-Object -First 1
}

function Get-PublicIp {
    (Invoke-RestMethod -Uri 'https://api.ipify.org' -TimeoutSec 10).Trim()
}
