set -euo pipefail
export PATH=/home/fadya/.dotnet10-10.0.404:$PATH
export DOTNET_CLI_HOME=/home/fadya/shopixy-subdomains-audit-r2/dotnet-home NUGET_PACKAGES=/home/fadya/.nuget/packages
export DOTNET_ROOT=/home/fadya/.dotnet10-10.0.404 DOTNET_ROLL_FORWARD=LatestMajor DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export ASPNETCORE_ENVIRONMENT=Test
export ConnectionStrings__DefaultConnection='Host=127.0.0.1;Port=1;Database=affiliate_schema_design;Username=fixture'
base=/mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-affiliate-remediation-20261115
cd "$base/source/PointOfSale.Infrastructure"
dotnet restore ../PointOfSale.API/PointOfSale.API.csproj --source /home/fadya/.nuget/packages > "$base/migration-script-restore.log" 2>&1
dotnet /home/fadya/.nuget/packages/dotnet-ef/10.0.15/tools/net8.0/any/dotnet-ef.dll migrations script \
    20261113140000_FinancialVoucherAuthority 20261115112011_AffiliateManualPayoutReturn --idempotent \
    --project . --startup-project ../PointOfSale.API --no-build --output "$base/affiliate-return-migration.sql" > "$base/migration-script.log" 2>&1
printf '%s\n' 'Generated additive migration SQL offline; no database connection or apply requested.'
