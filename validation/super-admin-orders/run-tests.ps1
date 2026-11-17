param([string]$Receipt = 'backend-orders', [string]$Filter = 'FullyQualifiedName~SuperAdminOrdersForensicTests')
$ErrorActionPreference = 'Stop'
$privateRoot = Join-Path $env:LOCALAPPDATA 'Temp\opencode'
$sdk = Join-Path $privateRoot 'orders-sdk-10.0.404\dotnet.exe'
if (!(Test-Path -LiteralPath $sdk)) { $sdk = Join-Path $privateRoot 'finance-sdk-10.0.404\dotnet.exe' }
if (!(Test-Path -LiteralPath $sdk)) { throw 'Verified .NET 10.0.404 SDK must be restored before running tests.' }
$env:FINANCE_TEST_POSTGRES = [IO.File]::ReadAllText((Join-Path $privateRoot 'finance-test-postgres-private.txt')) + ';Timeout=60;Command Timeout=120'
$env:Testing__TempRoot = Join-Path $privateRoot 'orders-tests'
$env:DataProtection__KeysPath = Join-Path $privateRoot 'orders-tests\keys'
$connection = [Net.Sockets.TcpClient]::new()
try { $connection.Connect('127.0.0.1', 55432) } finally { $connection.Dispose() }
$log = Join-Path $privateRoot "$Receipt-private.log"
$ErrorActionPreference = 'Continue'
& $sdk test PointOfSale.Tests/PointOfSale.Tests.csproj --no-restore --filter $Filter --logger "trx;LogFileName=$Receipt.trx" --results-directory validation/super-admin-orders/test-results --verbosity minimal *> $log
$code = $LASTEXITCODE
$receiptPath = Join-Path $PWD "validation/super-admin-orders/test-results/$Receipt.trx"
if (Test-Path -LiteralPath $receiptPath) {
    $xml = [IO.File]::ReadAllText($receiptPath).Replace("`r`n","`n")
    [IO.File]::WriteAllText($receiptPath, $xml, [Text.UTF8Encoding]::new($false))
}
Get-Content $log -Tail 45
exit $code
