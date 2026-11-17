$ErrorActionPreference = 'Stop'
# Local disposable test server only. No production connections are used.
wsl -d Ubuntu -u root -- pg_conftool 16 main set port 55432
if ($LASTEXITCODE -ne 0) { throw 'Could not configure local PostgreSQL port' }
wsl -d Ubuntu -u root -- pg_conftool 16 main set listen_addresses localhost
wsl -d Ubuntu -u root -- pg_ctlcluster 16 main restart
if ($LASTEXITCODE -ne 0) { throw 'Could not start local PostgreSQL' }
$password = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
$exists = 'SELECT 1 FROM pg_roles WHERE rolname = ''finance_audit_runner'';' | wsl -d Ubuntu -u postgres -- psql -p 55432 -At -d postgres
if ($exists -eq '1') {
    $sql = "ALTER ROLE finance_audit_runner WITH LOGIN CREATEDB NOSUPERUSER NOCREATEROLE NOREPLICATION PASSWORD '$password';"
} else {
    $sql = "CREATE ROLE finance_audit_runner WITH LOGIN CREATEDB NOSUPERUSER NOCREATEROLE NOREPLICATION PASSWORD '$password';"
}
$sql | wsl -d Ubuntu -u postgres -- psql -p 55432 -v ON_ERROR_STOP=1 -d postgres | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not configure disposable test role' }
$connection = "Host=127.0.0.1;Port=55432;Database=postgres;Username=finance_audit_runner;Password=$password;Include Error Detail=false"
$privatePath = Join-Path $env:LOCALAPPDATA 'Temp\opencode\finance-test-postgres-private.txt'
[IO.File]::WriteAllText($privatePath, $connection)
$password = $null
$sql = $null
Write-Output 'Local PostgreSQL ready on loopback:55432; disposable test credentials saved outside repository.'
