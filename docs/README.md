# AShareRadar 文档索引

本目录的文档按“当前有效交接、专题方案、历史交接”管理。

## 首先阅读

1. project-handoff-current.md：当前唯一推荐的总交接入口。
2. project-handoff-2026-08-09.md：2026-08-09 前后功能、UI、映射采集和运行风险的详细记录。
3. project-handoff-2026-08-09-packaging.md：打包、安装、运行数据和交付注意事项。

## 当前有效专题文档

| 文档 | 用途 |
| --- | --- |
| strategy-first-development-plan.md | 策略优先开发方向、策略分层、历史回放和暂停项 |
| feature-research-returns-backtest-2026-08-09.md | 命中后收益统计、回测和策略参数入口需求调研 |
| src/AShareRadar.ServiceHost/策略逻辑.md | 策略维护台账。每次策略规则变更必须同步记录 |

## 历史交接文档

以下文档保留用于追溯，不作为当前状态的唯一依据：

| 文档 | 说明 |
| --- | --- |
| engineering-handoff-2026-07-28.md | 早期工程结构和基础能力交接 |
| engineering-handoff-2026-07-31.md | A 股情绪模块阶段性交接 |
| project-handoff-2026-08-03.md | 运行、发布和机会池阶段性交接 |
| project-handoff-2026-08-04.md | 主线策略替换、安装包和 UI 阶段性交接 |

## 文档维护规则

1. 新工程师先读 project-handoff-current.md，再按专题需要阅读其他文档。
2. 新功能完成后，先更新总交接文档的当前状态和已知问题，再新增专题文档。
3. 策略规则只在 src/AShareRadar.ServiceHost/策略逻辑.md 维护，交接文档只引用结论和代码位置。
4. 不删除历史交接文档；如果内容失效，在索引中标记为历史，并在总交接文档记录替代结论。
5. 交接文档使用 UTF-8 编码，避免使用系统默认编码保存中文。
