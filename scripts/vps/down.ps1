<#
.SYNOPSIS
Destroys the forwarding VPS to stop billing. The SSH key and firewall group are free and are kept.
#>
. (Join-Path $PSScriptRoot 'common.ps1')

$instance = Get-VpsInstance
if (-not $instance) { Write-Host 'No VPS exists.'; return }
& $VultrCli instance delete $instance.id 2>$null
if ($LASTEXITCODE -ne 0) { throw "Deleting instance $($instance.id) failed." }
Write-Host "Deleted instance $($instance.id) ($($instance.main_ip))."
