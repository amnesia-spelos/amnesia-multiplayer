<#
.SYNOPSIS
    Packages the release archive a player extracts into their Amnesia folder.

.DESCRIPTION
    Builds the modified Amnesia.exe from the sibling amnesia-tdd-tcp checkout, tests and
    publishes the Game Peer, and lays both out with this repository's resources exactly as
    they sit in the game root. Writes the archive and its release notes under artifacts\release
    and prints - never runs - the gh commands that publish both GitHub releases.

    Both repositories must be clean and sitting exactly on a tag. -Dev lifts the tag
    requirement for a throwaway build and marks the archive name with the commit it came from.

.EXAMPLE
    .\scripts\package-release.ps1

.EXAMPLE
    .\scripts\package-release.ps1 -Dev
#>
[CmdletBinding()]
param(
    # Packages an untagged working build: skips the tag check and names the archive -dev+<short-sha>.
    [switch]$Dev,
    # The amnesia-tdd-tcp checkout that supplies Amnesia.exe.
    [string]$GameRepositoryPath
)

$ErrorActionPreference = 'Stop'
# Native exit codes are inspected deliberately below - a failing `git describe` is an answer, not an error.
$PSNativeCommandUseErrorActionPreference = $false

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if (-not $GameRepositoryPath) {
    $GameRepositoryPath = Join-Path (Split-Path -Parent $repositoryRoot) 'amnesia-tdd-tcp'
}

function Assert-RepositoryIsClean {
    param([string]$Path, [string]$Name)

    if (-not (Test-Path -LiteralPath (Join-Path $Path '.git'))) {
        throw "$Name is not a git checkout at $Path."
    }
    $status = & git -C $Path status --porcelain
    if ($LASTEXITCODE -ne 0) { throw "Could not read the git status of $Name at $Path." }
    if ($status) {
        throw "$Name has uncommitted changes; commit or stash them before packaging a release:`n$($status -join [Environment]::NewLine)"
    }
}

function Get-ExactTag {
    param([string]$Path)

    $tag = & git -C $Path describe --exact-match --tags HEAD 2>$null
    $described = $LASTEXITCODE -eq 0
    # An untagged HEAD is an answer, not a failure: don't leave git's exit code behind for the caller.
    $global:LASTEXITCODE = 0
    if (-not $described) { return $null }
    return "$tag".Trim()
}

function Get-HeadCommit {
    param([string]$Path, [switch]$Short)

    $arguments = @('rev-parse')
    if ($Short) { $arguments += '--short' }
    $arguments += 'HEAD'
    $commit = & git -C $Path @arguments
    if ($LASTEXITCODE -ne 0) { throw "Could not read HEAD of the checkout at $Path." }
    return "$commit".Trim()
}

function Format-TagOrDevBuild {
    param([string]$Tag)

    if ($Tag) { return $Tag }
    return '(untagged dev build)'
}

function Invoke-Step {
    param([string]$Description, [scriptblock]$Action)

    Write-Host ''
    Write-Host "==> $Description" -ForegroundColor Cyan
    & $Action
}

# --- What is being packaged -------------------------------------------------

$buildProperties = [xml](Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\Directory.Build.props') -Raw)
# SelectSingleNode rather than property access, which would quietly yield an array of every match.
$version = "$($buildProperties.SelectSingleNode('/Project/PropertyGroup/Version').InnerText)".Trim()
$codename = "$($buildProperties.SelectSingleNode('/Project/PropertyGroup/Codename').InnerText)".Trim()
if (-not $version -or -not $codename) {
    throw 'src\Directory.Build.props does not define both Version and Codename.'
}

$lanProtocolSource = Get-Content -LiteralPath (Join-Path $repositoryRoot 'src\Multimnesia.Contracts\LanProtocol.cs') -Raw
$lanProtocolMatch = [regex]::Match($lanProtocolSource, 'CurrentVersion\s*=\s*(\d+)')
if (-not $lanProtocolMatch.Success) { throw 'Could not read LanProtocol.CurrentVersion from the source.' }
$lanProtocolVersion = $lanProtocolMatch.Groups[1].Value

Assert-RepositoryIsClean -Path $repositoryRoot -Name 'This repository'
Assert-RepositoryIsClean -Path $GameRepositoryPath -Name 'The amnesia-tdd-tcp checkout'

$multiplayerCommit = Get-HeadCommit -Path $repositoryRoot
$gameCommit = Get-HeadCommit -Path $GameRepositoryPath
$multiplayerTag = Get-ExactTag -Path $repositoryRoot
$gameTag = Get-ExactTag -Path $GameRepositoryPath

if ($Dev) {
    $archiveVersion = "v$version-dev+$(Get-HeadCommit -Path $repositoryRoot -Short)"
}
else {
    if (-not $multiplayerTag) {
        throw "This repository's HEAD ($multiplayerCommit) is not exactly on a tag. Tag the release first, or pass -Dev for a throwaway build."
    }
    if (-not $gameTag) {
        throw "The amnesia-tdd-tcp HEAD ($gameCommit) is not exactly on a tag. Tag it first, or pass -Dev for a throwaway build."
    }
    if ($multiplayerTag -ne "v$version") {
        throw "This repository's tag is $multiplayerTag but src\Directory.Build.props says $version."
    }
    $archiveVersion = $multiplayerTag
}

$archiveName = "amnesia-multiplayer-$archiveVersion-$($codename.ToLowerInvariant())-win-x64"
Write-Host "Packaging $archiveName" -ForegroundColor Green
Write-Host "  multiplayer      $(Format-TagOrDevBuild $multiplayerTag) $multiplayerCommit"
Write-Host "  amnesia-tdd-tcp  $(Format-TagOrDevBuild $gameTag) $gameCommit"

# --- Build ------------------------------------------------------------------

$outputDirectory = Join-Path $repositoryRoot 'artifacts\release'
$stagingDirectory = Join-Path $outputDirectory 'staging'
if (Test-Path -LiteralPath $outputDirectory) { Remove-Item -LiteralPath $outputDirectory -Recurse -Force }
New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null

$gameExecutable = Join-Path $GameRepositoryPath 'artifacts\Release\Amnesia.exe'
# The copy inside the archive is what the gh command attaches, so a later tdd-tcp build cannot
# swap the binary out from under a release that was verified on two PCs.
$packagedGameExecutable = Join-Path $stagingDirectory 'Amnesia.exe'
Invoke-Step 'Building Amnesia.exe from amnesia-tdd-tcp' {
    & (Join-Path $GameRepositoryPath 'scripts\build-windows.ps1')
    if (-not (Test-Path -LiteralPath $gameExecutable)) {
        throw "The amnesia-tdd-tcp build did not produce $gameExecutable."
    }
}

Invoke-Step 'Testing the Game Peer' {
    & dotnet test (Join-Path $repositoryRoot 'src\Multimnesia.sln') --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "The Game Peer test suite failed with exit code $LASTEXITCODE." }
}

$gamePeerDirectory = Join-Path $stagingDirectory 'multiplayer'
Invoke-Step 'Publishing the Game Peer (win-x64, self-contained)' {
    & dotnet publish (Join-Path $repositoryRoot 'src\Multimnesia.Client\Multimnesia.Client.csproj') `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        -p:PublishTrimmed=false `
        -p:PublishSingleFile=false `
        --output $gamePeerDirectory
    if ($LASTEXITCODE -ne 0) { throw "Publishing the Game Peer failed with exit code $LASTEXITCODE." }
}

# --- Lay the archive out like the game root ---------------------------------

Invoke-Step 'Assembling the archive' {
    Copy-Item -LiteralPath $gameExecutable -Destination $packagedGameExecutable
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release-files\Start Multiplayer.bat') -Destination $stagingDirectory
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release-files\INSTALL.txt') -Destination $stagingDirectory

    # resources already mirrors the game root, so its content copies across wholesale.
    foreach ($content in @('custom_stories', 'entities')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot "resources\$content") -Destination $stagingDirectory -Recurse
    }

    $versionLines = @(
        "Amnesia Multiplayer v$version ""$codename"""
        ''
        "multiplayer tag:        $(Format-TagOrDevBuild $multiplayerTag)"
        "multiplayer commit:     $multiplayerCommit"
        "amnesia-tdd-tcp tag:    $(Format-TagOrDevBuild $gameTag)"
        "amnesia-tdd-tcp commit: $gameCommit"
        "LanProtocol version:    $lanProtocolVersion"
        "packaged:               $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')"
    )
    Set-Content -LiteralPath (Join-Path $stagingDirectory 'VERSION.txt') -Value $versionLines -Encoding utf8
}

$archivePath = Join-Path $outputDirectory "$archiveName.zip"
Invoke-Step "Writing $archiveName.zip" {
    Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
}

$releaseNotesPath = Join-Path $outputDirectory "$archiveName-release-notes.md"
$releaseNotes = @"
# Amnesia Multiplayer v$version "$codename"

A trusted-LAN, two-player Multiplayer Session for Amnesia: The Dark Descent. Two players host
and join over a private network, chat in game, start the same Custom Story together, and see
each other move.

## Install

Download ``$archiveName.zip`` and follow the ``INSTALL.txt`` inside it: back up your original
``Amnesia.exe``, extract the archive into your Amnesia folder, allow inbound TCP 5000 on the
private network profile of the PC that hosts, and run ``Start Multiplayer.bat``. Both players
need this same archive.

> [!WARNING]
> The Multiplayer Relay is unauthenticated and unencrypted. Run it only between trusted
> computers on a trusted, private LAN. Never expose its port to the internet.

## What is in the archive

| | |
|---|---|
| Multiplayer | $(Format-TagOrDevBuild $multiplayerTag) (``$multiplayerCommit``) |
| ``amnesia-tdd-tcp`` | $(Format-TagOrDevBuild $gameTag) (``$gameCommit``) |
| ``LanProtocol`` version | $lanProtocolVersion |

## Known limitations

- Custom Stories only; the main game is not supported.
- Map changes and returning to the main menu are not shared.
- An Avatar stays frozen at its last Pose while its player is in the main menu.
- Enemies ignore the Joining Player.

See the README for the full list and the two-PC walkthrough.
"@
Set-Content -LiteralPath $releaseNotesPath -Value $releaseNotes -Encoding utf8

# --- Publishing stays a human step ------------------------------------------

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host "  archive        $archivePath"
Write-Host "  release notes  $releaseNotesPath"
Write-Host "  staging        $stagingDirectory (the archive, unzipped; kept so you can inspect the layout)"
Write-Host ''

if ($Dev) {
    Write-Host 'This is a -Dev build and is not publishable. Tag both repositories and run the script again to get the release commands.' -ForegroundColor Yellow
    return
}

Write-Host 'Verify the archive on two PCs (docs\RELEASING.md), then run these yourself:' -ForegroundColor Yellow
Write-Host ''
Write-Host @"
gh release create $gameTag ``
    --repo amnesia-spelos/amnesia-tdd-tcp ``
    --title '$gameTag' ``
    --prerelease ``
    --notes 'The Amnesia build packaged with Amnesia Multiplayer $multiplayerTag "$codename".' ``
    '$packagedGameExecutable'

gh release create $multiplayerTag ``
    --repo amnesia-spelos/amnesia-multiplayer ``
    --title '$codename ($multiplayerTag)' ``
    --prerelease ``
    --notes-file '$releaseNotesPath' ``
    '$archivePath'
"@
