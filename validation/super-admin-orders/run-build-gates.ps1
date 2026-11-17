$ErrorActionPreference = 'Stop'
$root = Join-Path $env:LOCALAPPDATA 'Temp\opencode'
$sdk = Join-Path $root 'orders-sdk-10.0.404\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk = Join-Path $root 'finance-sdk-10.0.404\dotnet.exe' }
if (!(Test-Path -LiteralPath $sdk)) { throw 'Verified .NET 10.0.404 SDK must be restored before building.' }
function SourceDigest {
    $paths = git ls-files -c -o --exclude-standard PointOfSale.API PointOfSale.Core PointOfSale.Infrastructure PointOfSale.Web PointOfSale.Tests PointOfSale.Storefront theme-authority
    $lines = @($paths | Sort-Object -Unique | Where-Object { $_ -match '\.(cs|csproj|json|ts|tsx|js|jsx|css|html)$' } | ForEach-Object {
        "$_ $((Get-FileHash -Algorithm SHA256 -LiteralPath $_).Hash.ToLowerInvariant())"
    })
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
}
$before = SourceDigest
$revision = git rev-parse HEAD
$cleanBefore = !(git status --porcelain --untracked-files=all)
if (!$cleanBefore) { throw 'Commit all reviewed source before running sealed build gates.' }
$ErrorActionPreference = 'Continue'
& $sdk build PointOfSale.API/PointOfSale.API.csproj --no-restore --no-incremental -c Release -v minimal *> (Join-Path $root 'orders-sealed-api-build-private.log')
$apiCode = $LASTEXITCODE
Push-Location PointOfSale.Web
try {
    npx tsc --noEmit *> (Join-Path $root 'orders-sealed-typecheck-private.log')
    $typeCode = $LASTEXITCODE
    npm run build *> (Join-Path $root 'orders-sealed-web-build-private.log')
    $webCode = $LASTEXITCODE
} finally { Pop-Location }
$ErrorActionPreference = 'Stop'
$after = SourceDigest
$cleanAfter = !(git status --porcelain --untracked-files=all)
$sourceFiles = @{}
git ls-files -c -o --exclude-standard PointOfSale.API PointOfSale.Core PointOfSale.Infrastructure PointOfSale.Web PointOfSale.Tests PointOfSale.Storefront theme-authority |
    Sort-Object -Unique | Where-Object { $_ -match '\.(cs|csproj|json|ts|tsx|js|jsx|css|html)$' } | ForEach-Object {
        $sourceFiles[$_] = (Get-FileHash -Algorithm SHA256 -LiteralPath $_).Hash.ToLowerInvariant()
    }
$assemblies = @{}
foreach ($name in @('PointOfSale.API.dll','PointOfSale.Core.dll','PointOfSale.Infrastructure.dll')) {
    $assemblies[$name] = (Get-FileHash -Algorithm SHA256 "PointOfSale.API/bin/Release/net10.0/$name").Hash.ToLowerInvariant()
}
$webArtifacts = @{}
$distRoot = (Resolve-Path 'PointOfSale.Web/dist').Path
Get-ChildItem -LiteralPath $distRoot -File -Recurse | ForEach-Object {
    $relative = $_.FullName.Substring($distRoot.Length + 1).Replace('\','/')
    $webArtifacts[$relative] = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant()
}
$result = @{
    capturedAtUtc = [DateTime]::UtcNow.ToString('O')
    sourceCommit = $revision
    sourceCheckoutCleanBeforeBuild = $cleanBefore
    sourceCheckoutCleanAfterBuild = $cleanAfter
    apiReleaseExitCode = $apiCode
    webTypecheckExitCode = $typeCode
    webBuildExitCode = $webCode
    sourceSha256BeforeBuild = $before
    sourceSha256AfterBuild = $after
    sourceUnchangedDuringBuild = ($before -eq $after)
    sourceFilesSha256 = $sourceFiles
    apiArtifactSha256 = $assemblies
    webArtifactSha256 = $webArtifacts
    webIndexSha256 = (Get-FileHash -Algorithm SHA256 PointOfSale.Web/dist/index.html).Hash.ToLowerInvariant()
    pass = ($apiCode -eq 0 -and $typeCode -eq 0 -and $webCode -eq 0 -and $before -eq $after -and $cleanBefore -and $cleanAfter)
}
[IO.File]::WriteAllText((Join-Path $PWD 'validation/super-admin-orders/build-gates.json'),
    (($result | ConvertTo-Json -Depth 10) + "`n"), [Text.UTF8Encoding]::new($false))
[PSCustomObject]$result | Select-Object capturedAtUtc,apiReleaseExitCode,webTypecheckExitCode,webBuildExitCode,sourceUnchangedDuringBuild,pass | ConvertTo-Json
if (!$result.pass) { exit 1 }
