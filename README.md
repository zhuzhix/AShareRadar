# AShareRadar

AShareRadar is a Windows desktop and local service application for A-share market monitoring, strategy scanning, review, and next-day prediction research.

The solution contains:

- `AShareRadar.ServiceHost`: local HTTP API, background workers, market-data update jobs, strategy scanning, review APIs.
- `AShareRadar.Desktop`: WPF desktop client.
- `AShareRadar.Application`: application services and strategy orchestration.
- `AShareRadar.Strategies`: intraday and research strategy implementations.
- `AShareRadar.Infrastructure`: market data providers, DuckDB cache, EastMoney/GM SDK integration.
- `AShareRadar.Persistence`: SQLite persistence.
- `tools`: data update, market mapping, research, and Qlib next-day prediction scripts.
- `packaging/windows`: installation and runtime helper scripts.

## Requirements

- Windows x64
- .NET 8 SDK for development
- .NET 8 Desktop Runtime and ASP.NET Core Runtime for framework-dependent deployment
- Python 3.12+ for data update scripts
- EastMoney/GM SDK token in environment variable `EASTMONEY_QUANT_TOKEN`

The project references the EastMoney .NET SDK assembly at:

```text
lib/eastmoney/gmsdk-net-x64.dll
```

Native SDK runtime DLLs may be required when running the service, depending on the target deployment layout.

## Configuration

Main service configuration:

```text
src/AShareRadar.ServiceHost/appsettings.json
```

Important paths are relative by default:

```text
data/ashare.duckdb
data/runtime/ashare-radar.sqlite
tools/eastmoney_quant/*.py
tools/qlib_next_day/*.ps1
data/qlib/...
```

Large runtime data is intentionally not committed. Prepare it locally before running production-style scans.

## Runtime Data

Not tracked in Git:

- `data/ashare.duckdb`: historical daily/weekly/minute market data.
- `data/runtime/ashare-radar.sqlite`: app runtime state, opportunities, review records, sentiment snapshots.
- `data/qlib/**`: Qlib signal and next-day prediction outputs.
- `artifacts/**`: publish and verification output.
- `.run/**`: local process logs and temporary runtime files.

Tracked seed/config data:

```text
src/AShareRadar.ServiceHost/data/sector-mapping.csv
src/AShareRadar.ServiceHost/data/concept-mapping.csv
src/AShareRadar.ServiceHost/data/market-sentiment-external.csv
src/AShareRadar.ServiceHost/data/trading-calendar-cn.json
```

## Build

```powershell
dotnet restore .\AShareRadar.slnx
dotnet build .\AShareRadar.slnx -c Release
```

Run strategy smoke tests:

```powershell
dotnet run --project .\tests\AShareRadar.Strategies.Tests\AShareRadar.Strategies.Tests.csproj -c Release --no-restore
```

## Run Locally

Start the service:

```powershell
dotnet run --project .\src\AShareRadar.ServiceHost\AShareRadar.ServiceHost.csproj --urls http://127.0.0.1:18730
```

Start the desktop app:

```powershell
dotnet run --project .\src\AShareRadar.Desktop\AShareRadar.Desktop.csproj
```

Useful health checks:

```powershell
Invoke-RestMethod http://127.0.0.1:18730/api/monitor/status
Invoke-RestMethod "http://127.0.0.1:18730/api/market-sentiment/snapshot?refresh=true"
```

## Publish

ServiceHost verification output:

```powershell
dotnet publish .\src\AShareRadar.ServiceHost\AShareRadar.ServiceHost.csproj -c Release --no-restore -o .\artifacts\verify-servicehost
```

Desktop verification output:

```powershell
dotnet publish .\src\AShareRadar.Desktop\AShareRadar.Desktop.csproj -c Release --no-restore -o .\artifacts\verify-desktop
```

## Packaging

Windows packaging scripts live in:

```text
packaging/windows
```

The intended package layout is:

```text
app/service
app/desktop
data
tools
start-ashare-radar.ps1
stop-ashare-radar.ps1
install.ps1
```

Generated packages and published binaries should remain under `artifacts/` and are ignored by Git.

## Notes

- Do not commit runtime databases, logs, Qlib outputs, or generated prediction CSV files.
- Keep credentials outside the repo. Use `EASTMONEY_QUANT_TOKEN` or local user secrets.
- If a local service is already running on `127.0.0.1:18730`, stop it before publishing over an existing verification directory.
