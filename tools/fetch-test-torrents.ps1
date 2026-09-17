<#
.SYNOPSIS
    Downloads current Debian and Ubuntu torrent files into the test fixtures.

.DESCRIPTION
    The fixtures are committed, so this is only needed when a release is retired
    and its torrent has to be replaced. Whatever it fetches, the expected values
    in TorrentFileTests.cs have to be recomputed to match — they are deliberately
    not derived from this project's own parser.
#>

[CmdletBinding()]
param(
    [string] $Destination = (Join-Path $PSScriptRoot '..\tests\Bitfield.Tests\Fixtures')
)

$ErrorActionPreference = 'Stop'

$Destination = (Resolve-Path $Destination).Path
Write-Host "Fetching into $Destination"

function Get-Torrent {
    param([string] $IndexUrl, [string] $Pattern)

    $index = Invoke-WebRequest -Uri $IndexUrl -UseBasicParsing
    $name = ($index.Content | Select-String -Pattern $Pattern -AllMatches).Matches |
        ForEach-Object { $_.Value } |
        Sort-Object -Unique |
        Select-Object -Last 1

    if (-not $name) {
        Write-Warning "no torrent matching $Pattern at $IndexUrl"
        return
    }

    $target = Join-Path $Destination $name
    Invoke-WebRequest -Uri ($IndexUrl.TrimEnd('/') + '/' + $name) -OutFile $target -UseBasicParsing
    Write-Host ("  {0}  {1:N0} bytes" -f $name, (Get-Item $target).Length)
}

Get-Torrent -IndexUrl 'https://cdimage.debian.org/debian-cd/current/amd64/bt-cd/' `
    -Pattern 'debian-[0-9.]+-amd64-netinst\.iso\.torrent'

Get-Torrent -IndexUrl 'https://releases.ubuntu.com/24.04/' `
    -Pattern 'ubuntu-[0-9.]+-desktop-amd64\.iso\.torrent'

Write-Host 'Done. Recompute the expected values in TorrentFileTests.cs for anything replaced.'
