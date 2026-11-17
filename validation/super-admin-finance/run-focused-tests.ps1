param([string]$Receipt = 'backend-focused-postgres', [string]$Filter = '')
$ErrorActionPreference = 'Stop'
$privateRoot = Join-Path $env:LOCALAPPDATA 'Temp\opencode'
$env:FINANCE_TEST_POSTGRES = [IO.File]::ReadAllText((Join-Path $privateRoot 'finance-test-postgres-private.txt'))
$env:Testing__TempRoot = Join-Path $privateRoot 'finance-tests'
$env:DataProtection__KeysPath = Join-Path $privateRoot 'finance-tests\keys'
$connection = [Net.Sockets.TcpClient]::new()
try { $connection.Connect('127.0.0.1', 55432) } finally { $connection.Dispose() }
if (!$Filter) {
    $Filter = 'FullyQualifiedName~SuperAdminFinancialDashboardServiceTests|FullyQualifiedName~RefundFinancialCalculatorTests|FullyQualifiedName~CommerceHubFinancialLedgerServiceTests|FullyQualifiedName~FinancialReconciliationServiceTests|FullyQualifiedName~CommerceHubFinanceReconciliationReadServiceTests|FullyQualifiedName~SuperAdminAuthorizationTests|FullyQualifiedName~SuperAdminPlatformAuthorityTests|FullyQualifiedName~CommissionServiceTests|FullyQualifiedName~CommerceAccountingServiceTests|FullyQualifiedName~CommerceHubRefundHardeningTests|FullyQualifiedName~CommerceHubRefundCertificationTests|FullyQualifiedName~RefundScopeIsolationAuthorityCertificationTests'
}
$log = Join-Path $privateRoot "$Receipt-private.log"
$ErrorActionPreference = 'Continue' # Native stderr must not abort TRX/exit-code collection.
& (Join-Path $privateRoot 'finance-sdk-10.0.404\dotnet.exe') test PointOfSale.Tests/PointOfSale.Tests.csproj --no-restore --filter $Filter --logger "trx;LogFileName=$Receipt.trx" --results-directory validation/super-admin-finance/test-results --verbosity minimal *> $log
$code = $LASTEXITCODE
$ErrorActionPreference = 'Stop'
Get-Content $log -Tail 60
exit $code
