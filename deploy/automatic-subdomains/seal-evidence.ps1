$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$output = Join-Path $root 'SHA256SUMS.txt'

$lines = Get-ChildItem $root -File |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }

[IO.File]::WriteAllLines($output, $lines, (New-Object Text.UTF8Encoding($false)))
Write-Output "sealed $($lines.Count) automatic-subdomain artifacts"
