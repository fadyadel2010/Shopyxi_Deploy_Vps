set -euo pipefail
export DOTNET_ROOT=/dotnet DOTNET_CLI_HOME=/tmp/analytics-tests NUGET_PACKAGES=/packages
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
export Testing__TempRoot=/tmp/analytics-tests DataProtection__KeysPath=/tmp/analytics-tests/keys
export POINT_OF_SALE_SOLUTION_ROOT=/src
cd /src
/dotnet/dotnet test PointOfSale.Tests/PointOfSale.Tests.csproj -c Release --no-build --no-restore -m:1 \
  -p:UseSharedCompilation=false --filter 'FullyQualifiedName~AffiliateAnalyticsConversionBridgeTests|FullyQualifiedName~MongoIndexManagementPolicyTests' \
  --logger 'trx;LogFileName=conversion-bridge.trx' --results-directory /src/validation/affiliate-analytics-conversion-bridge/results
