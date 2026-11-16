set -euo pipefail
export DOTNET_ROOT=/dotnet DOTNET_CLI_HOME=/tmp/analytics-migrate NUGET_PACKAGES=/packages ASPNETCORE_ENVIRONMENT=Test
cd /src/PointOfSale.Infrastructure
/dotnet/dotnet /packages/dotnet-ef/10.0.15/tools/net8.0/any/dotnet-ef.dll database update 20261115213516_AffiliateAnalyticsConversionBridge \
  --project . --startup-project ../PointOfSale.API --configuration Release --no-build
