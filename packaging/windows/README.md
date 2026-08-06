# AShareRadar Windows 安装包

## 安装

在 PowerShell 中执行：

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

默认安装到：

```text
%LOCALAPPDATA%\AShareRadar
```

安装脚本会检查并安装：

- .NET 8 Desktop Runtime
- .NET 8 ASP.NET Core Runtime
- Python 3.12
- 历史数据更新脚本所需 Python 包

## 启动

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\AShareRadar\start-ashare-radar.ps1"
```

## 停止

```powershell
powershell -ExecutionPolicy Bypass -File "$env:LOCALAPPDATA\AShareRadar\stop-ashare-radar.ps1"
```

## 数据

安装包内已包含：

- SQLite 运行库数据：`app\service\data\runtime\ashare-radar.sqlite`
- DuckDB 历史 K 线数据：`app\service\data\ashare.duckdb`
- 行业/概念映射：`app\service\data\sector-mapping.csv`、`app\service\data\concept-mapping.csv`
- 历史数据自动更新脚本：`app\service\tools\history_update`
