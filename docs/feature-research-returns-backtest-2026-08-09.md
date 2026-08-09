# AShareRadar 需求调研文档：命中后收益统计与回测参数入口

日期：2026-08-09
调研范围：`AShareRadar.ServiceHost`、`AShareRadar.Application`、`AShareRadar.Persistence`、`AShareRadar.Desktop`

## 1. 需求背景

用户希望新增三类能力：

1. 策略命中后的未来 `1 / 3 / 5` 天收益和亏损统计，用于：
   - 主线板块共振
   - 主线低开高走
2. 策略命中后的未来 `1 周 / 1 月 / 3 月` 收益和亏损统计，用于：
   - 非主线板块策略
   - 主线低开高走之外的其他策略
3. 提供回测功能，并支持：
   - 判断当前回测是否缺少策略命中数据
   - 提供修改策略参数的入口

## 2. 当前项目现状

### 2.1 已有的相关能力

项目里已经存在“命中事件”和“回测 replay”的基础链路：

- `signal_events` / `strategy_hits` 保存了策略命中事件和策略明细。
- `HistoricalSignalItem` 已包含：
  - `EventTime`
  - `StrategyCode`
  - `StrategyName`
  - `Score`
  - `Price`
  - `Reason`
  - `Risk`
  - `StrategyHitCount`
- `BacktestReplayService` 已可回放历史区间，并输出：
  - 1 日收益
  - 3 日收益
  - 5 日收益
  - 1/3/5 日胜率
  - 1/3/5 日平均收益
  - 5 日最好 / 最差
- `PredictionReviewService` 已对“次日预测”提供生成、校验和持久化。
- `LongTermTrackingService` 已将非主线策略命中沉淀到长期跟踪库。

### 2.2 已有但不完整的部分

当前实现只覆盖了“短周期收益验证”的一部分：

- `BacktestSignalItem` 只有 `Return1Day / Return3Day / Return5Day`。
- `BacktestStrategySummaryItem` 只有 1/3/5 日相关汇总。
- `PredictionReviewService.VerifyAsync` 只验证“次日”：
  - 次日开盘收益
  - 次日收盘收益
  - 次日最高 / 最低收益
- `LongTermTrackingItem` 目前只保存：
  - 首次命中时间
  - 最近命中时间
  - 命中次数
  - 最新价 / 最新分数 / 最佳分数
  - 最近原因 / 风险 / 状态 / 备注 / 标签
  - 没有保存命中后多周期收益统计

### 2.3 策略参数入口现状

调研结果表明：

- 策略支持参数注入：
  - `StrategyDefinition.Parameters`
  - `StrategyContext.Parameters`
  - `MainSectorResonanceStrategy` 已通过 `GetDecimalParameter` / `GetIntParameter` 读取参数
- 但仓库中没有现成的“策略参数配置持久化 + UI 编辑 + 回测注入”完整链路。
- 数据库里原先的 `strategy_parameter_profiles` 表已经在 `SqliteDatabase` 的清理逻辑中被移除。
- 也没有找到可直接复用的“策略参数管理页面”或“参数 profile API”。

结论：
当前项目“能读参数，但不能长期保存和回放参数配置”。

## 3. 现有数据是否足够支撑需求

### 3.1 命中后未来 1 / 3 / 5 天收益统计

可行性：高。

原因：
- 命中事件有 `EventTime` 和 `Price`
- 日线数据可通过 `IKLineDataProvider` 获取
- `BacktestReplayService` 已有前向收益计算方法

需要补充的能力：
- 统一定义“命中价”口径
- 统一定义“收益/亏损”口径
- 如果命中当天是盘中信号，需要明确：
  - 用信号当时价格作为入场价
  - 还是用收盘价 / 次日开盘价作为入场价

### 3.2 命中后未来 1 周 / 1 月 / 3 月收益统计

可行性：高。

原因：
- 日线 K 线已可按交易日顺序取值
- 未来 1 周 / 1 月 / 3 月可按“前向交易日数”或“前向自然月/自然周”两种方式实现

需要先定口径：
- 推荐口径 1：按交易日前向窗口
  - 1 周 = 5 个交易日
  - 1 月 = 20 个交易日
  - 3 月 = 60 个交易日
- 推荐口径 2：按自然日窗口
  - 更贴近日历，但会遇到节假日和停牌问题

从当前项目数据结构看，推荐先用“交易日窗口”，实现简单且与回测体系一致。

### 3.3 回测是否缺少“策略命中数据”

结论：**回测不缺“命中事件”的基础数据，但缺“策略参数版本化”和“按指定参数重放”的能力**。

现状：
- 回测能从策略注册表拿到当前启用策略
- 回测能在历史日线/周线/分钟线中重放并重新跑策略
- 但当前没有一套稳定的“策略命中快照”作为独立回测输入集
- 也没有参数 profile 作为回测快照的一部分

所以如果问“回测是否缺少策略命中的数据”：
- 对已有历史命中事件的统计，不缺
- 对“按某个历史策略版本 + 某套参数”重跑，不够

## 4. 需求拆解与建议方案

### 4.1 未来收益统计功能

建议拆成两个服务层：

#### A. 命中后短周期收益统计
用于：
- 主线板块共振
- 主线低开高走

统计维度建议：
- 按策略
- 按股票
- 按命中事件
- 按命中日期

建议字段：
- 命中数
- 1 日收益均值 / 中位数 / 胜率 / 最大回撤
- 3 日收益均值 / 中位数 / 胜率 / 最大回撤
- 5 日收益均值 / 中位数 / 胜率 / 最大回撤
- 最佳案例 / 最差案例

#### B. 命中后中长周期收益统计
用于：
- 非主线板块策略
- 主线低开高走之外的其他策略

统计维度建议：
- 1 周
- 1 月
- 3 月

建议字段：
- 收益均值
- 胜率
- 中位数
- 最大上涨
- 最大回撤
- 样本数量

### 4.2 回测功能

建议把回测拆成两层：

#### A. 命中回放回测
输入：
- 时间区间
- 股票池
- 策略集合
- 是否使用当前参数

输出：
- 命中明细
- 策略汇总
- 按策略的收益统计

#### B. 参数版本回测
输入：
- 时间区间
- 股票池
- 策略代码
- 参数集 ID / 参数快照

输出：
- 命中次数
- 收益统计
- 参数对比结果

### 4.3 策略参数入口

建议增加一个正式的“策略参数配置”能力，而不是只在代码里改默认值。

最小可行方案：
- 为每个策略保存一份参数 JSON
- 回测时可选择某个参数版本
- 实盘扫描时也可加载指定参数版本

推荐增加的能力：
- 新增 / 编辑 / 复制 / 删除参数配置
- 参数版本号
- 生效范围
- 说明备注
- 最近使用时间

## 5. 影响范围评估

### 5.1 受影响模块

- `AShareRadar.Application.Review`
- `AShareRadar.Application.Backtesting`
- `AShareRadar.Application.History`
- `AShareRadar.Persistence.Review`
- `AShareRadar.Persistence.Database`
- `AShareRadar.ServiceHost.Program`
- `AShareRadar.Desktop.MainWindow.xaml`
- `AShareRadar.Desktop.MainWindow.xaml.cs`

### 5.2 可能新增的数据结构

建议考虑新增：

- `strategy_return_statistics`
- `strategy_return_statistics_items`
- `strategy_parameter_profiles`
- `strategy_parameter_profile_items`

也可以先不新表，先做内存统计和导出，但不利于长期复盘和页面查询。

## 6. 风险与约束

1. 命中收益口径必须先统一，否则不同页面会出现数据不一致。
2. 盘中命中信号的入场价口径要明确，否则 1 日 / 3 日收益会偏差很大。
3. 1 周 / 1 月 / 3 月建议先按交易日窗口，不建议第一版直接按自然日。
4. 参数入口如果直接放进回测而不做版本化，后续很难追溯“哪套参数对应哪次结果”。
5. 如果策略参数可自由编辑，必须保留默认参数和恢复默认功能。

## 7. 调研结论

### 可以做

- 命中后 1 / 3 / 5 天收益统计
- 命中后 1 周 / 1 月 / 3 月收益统计
- 回测中追加收益统计和命中明细
- 策略参数配置入口

### 需要先定口径

- 命中价采用哪一个价格
- 1 周 / 1 月 / 3 月按交易日还是自然日
- 回测是否只做历史命中回放，还是支持参数版本回测

### 推荐实施顺序

1. 先做命中后收益统计服务，统一口径。
2. 再做回测增强，补上统计汇总和命中明细导出。
3. 最后补策略参数配置入口和参数版本管理。

## 8. 相关代码位置

- `src/AShareRadar.Application/Review/PredictionReviewService.cs`
- `src/AShareRadar.Application/Review/LongTermTracking.cs`
- `src/AShareRadar.Persistence/Review/SqliteLongTermTrackingStore.cs`
- `src/AShareRadar.Application/Backtesting/BacktestReplayService.cs`
- `src/AShareRadar.Application/Backtesting/BacktestReplayResult.cs`
- `src/AShareRadar.Application/History/HistoricalSignalItem.cs`
- `src/AShareRadar.Application/History/HistoricalSignalQuery.cs`
- `src/AShareRadar.Strategies/Intraday/MainSectorResonanceStrategy.cs`
- `src/AShareRadar.Application/Strategies/StrategyContext.cs`
- `src/AShareRadar.Persistence/Database/SqliteDatabase.cs`
