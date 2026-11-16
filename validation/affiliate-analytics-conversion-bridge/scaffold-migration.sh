set -euo pipefail
export PATH=/home/fadya/.dotnet10-10.0.404:/opt/shopixy-toolchain/bin:$PATH
export DOTNET_ROOT=/dotnet DOTNET_CLI_HOME=/tmp/analytics-ef
export NUGET_PACKAGES=/packages ASPNETCORE_ENVIRONMENT=Test
export ConnectionStrings__DefaultConnection='Host=127.0.0.1;Port=1;Database=affiliate_analytics_design;Username=fixture'
cd /src/PointOfSale.Infrastructure
/dotnet/dotnet /packages/dotnet-ef/10.0.15/tools/net8.0/any/dotnet-ef.dll migrations add AffiliateAnalyticsConversionBridge \
  --project . --startup-project ../PointOfSale.API --configuration Release --no-build --output-dir Migrations
