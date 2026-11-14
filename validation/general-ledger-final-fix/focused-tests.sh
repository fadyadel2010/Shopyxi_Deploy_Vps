set -euo pipefail
label="$1"
case "$label" in before|after) ;; *) exit 2;; esac
export PATH="/home/fadya/.dotnet10-10.0.404:/opt/shopixy-toolchain/bin:$PATH"
export DOTNET_CLI_HOME=/home/fadya/shopixy-subdomains-audit-r2/dotnet-home NUGET_PACKAGES=/home/fadya/.nuget/packages DOTNET_NOLOGO=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
e=/mnt/d/Shopixy-Automatic-Subdomains-Audit-R2-20261111/source/validation/general-ledger-final-fix
cd /mnt/c/Users/fadya/AppData/Local/Temp/opencode/shopixy-general-ledger-final-fix/source
dotnet build validation/general-ledger-final-fix/GeneralLedgerRegression.csproj --no-incremental -m:1 -p:UseSharedCompilation=false > "$e/build-$label.log" 2>&1
dotnet test validation/general-ledger-final-fix/GeneralLedgerRegression.csproj --no-build --no-restore -m:1 --filter 'FullyQualifiedName~GeneralLedgerRunningBalanceTests|FullyQualifiedName~StockCountStaleProductionErrorContractTests' --logger "trx;LogFileName=$label.trx" --results-directory "$e" > "$e/tests-$label.log" 2>&1
