# 开发环境后端脚本

这些脚本只用于开发环境管理 `AShareRadar.ServiceHost`，避免后端进程残留、DLL 被占用、接口验证长时间无响应。

## 启动

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\dev\start-servicehost.ps1 -DisableHistoryUpdate
```

默认行为：

- 启动 `AShareRadar.ServiceHost`
- 写入 `.run/servicehost.pid`
- 写入 `.run/servicehost.port`
- 最多等待 15 秒检测 `http://127.0.0.1:{Port}/api/monitor/status`
- 如果服务未就绪，会自动停止进程，避免留下后台进程

常用参数：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\dev\start-servicehost.ps1 -Port 18731 -NoBuild -DisableHistoryUpdate -StartupTimeoutSeconds 8
```

只启动、不等待就绪：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\dev\start-servicehost.ps1 -NoWait
```

## 停止

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\dev\stop-servicehost.ps1
```

停止脚本只处理：

- `.run/servicehost.pid` 记录的进程
- 命令行包含 `AShareRadar.ServiceHost` 且路径属于当前项目的 `dotnet` 进程

不会停止其他项目或系统里的普通 `dotnet` 进程。

## 重启

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\dev\restart-servicehost.ps1 -DisableHistoryUpdate
```

## 故障定位

这套脚本的目标是让开发验证命令有明确结果：成功、已存在、启动失败、启动超时，不再无限等待。后续如果还需要更细的失败原因，再把后端日志落盘接进来。
