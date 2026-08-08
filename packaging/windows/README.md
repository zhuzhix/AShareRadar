# AShareRadar Windows Install Package

This package installs AShareRadar as a directory-style Windows application.

## Layout

```text
AShareRadar-Setup/
  install.ps1
  uninstall.ps1
  upgrade.ps1
  doctor.ps1
  start-ashare-radar.ps1
  stop-ashare-radar.ps1
  README.md
  package-manifest.json
  app/
    service/
    desktop/
  data/
    ashare.duckdb
    runtime/
    sector-mapping.csv
    concept-mapping.csv
    market-sentiment-external.csv
    trading-calendar-cn.json
  tools/
    eastmoney_quant/
    qlib_next_day/
    python/requirements.txt
  runtimes/
    installers/
```

## Install

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

Default install directory:

```text
%LOCALAPPDATA%\AShareRadar
```

To install with an SDK token:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Token "<EASTMONEY_QUANT_TOKEN>"
```

Skip desktop shortcut creation:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -NoShortcut
```

The installer rewrites `app\service\appsettings.json` so all data and script paths point to the install directory.

## Start and Stop

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\AShareRadar\start-ashare-radar.ps1"
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\AShareRadar\stop-ashare-radar.ps1"
```

## Upgrade

```powershell
powershell -ExecutionPolicy Bypass -File .\upgrade.ps1
```

Upgrade replaces `app`, `tools`, and package scripts, then rewrites config. Existing `data\runtime` is preserved.

## Doctor

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\AShareRadar\doctor.ps1"
```

Doctor checks executables, data files, Python, token, tools, and the local service health endpoint.

## Uninstall

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\AShareRadar\uninstall.ps1"
```

Keep data while removing application files:

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\AShareRadar\uninstall.ps1" -KeepData
```
