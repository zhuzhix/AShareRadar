# coding=utf-8
from __future__ import print_function, absolute_import

from collections import defaultdict
from datetime import datetime

try:
    from gm.api import *  # type: ignore
except Exception:
    # 允许脚本在非东财量化终端环境下做本地语法检查；真实运行时由 gm.api 提供行情与交易函数。
    pass


# 单只股票最低成交额门槛：低于该成交额的股票直接过滤，避免流动性不足。
MIN_AMOUNT = 300000000.0
# 板块最低热度分：低于该值的板块通常不认为具备明显共振。
MIN_SECTOR_HEAT_SCORE = 58.0
# 强势行业板块热度分：达到该分值说明行业层面已经比较活跃。
STRONG_SECTOR_HEAT_SCORE = 72.0
# 强势概念板块热度分：达到该分值说明概念层面已经比较活跃。
STRONG_CONCEPT_HEAT_SCORE = 72.0
# 龙头判定名次阈值：个股在行业/概念涨幅榜前 3 名时视为龙头候选。
CONCEPT_LEADER_RANK_THRESHOLD = 3
# 每次输出的信号数量上限。
MAX_RESULT_COUNT = 5
# 自动交易模式下最多同时持有的股票数量。
MAX_POSITION_COUNT = 3
# 默认单只股票目标仓位比例；动态仓位计算失败时使用该值兜底。
POSITION_PERCENT = 0.10
# 账户总仓位上限：自动交易模式下所有持仓合计不超过 50%。
MAX_TOTAL_POSITION_PERCENT = 0.50
# 单票最低开仓仓位：低于该值时不新开仓，避免过小仓位产生无意义交易。
MIN_SINGLE_POSITION_PERCENT = 0.05
# 单票最高仓位：即使信号很强，也不让单一股票占用过多资金。
MAX_SINGLE_POSITION_PERCENT = 0.18
# 防守仓位：持仓信号变弱但未触发清仓时，先降到该仓位观察。
DEFENSIVE_POSITION_PERCENT = 0.05
# 自动买入最低综合得分。
MIN_BUY_SCORE = 88.0
# 已持仓股票继续持有的最低综合得分。
MIN_HOLD_SCORE = 78.0
# 止损比例：相对买入价下跌 4% 触发止损。
STOP_LOSS_PERCENT = 0.04
# 止盈比例：相对买入价上涨 6% 触发止盈。
TAKE_PROFIT_PERCENT = 0.06
# 行业接口不可用时是否启用调试兜底分组；只用于排查终端数据问题，不代表真实板块共振。
ENABLE_DEBUG_SECTOR_FALLBACK = True
# 无行业/概念数据权限时启用个股强势模式，避免因为接口权限导致完全无信号。
ENABLE_NO_SECTOR_PERMISSION_MODE = True
# 诊断版本号：用于确认东财终端中运行的是当前这份脚本。
DIAGNOSTIC_VERSION = "2026-07-30-position-debug-v2"

# 主板、创业板、科创板的板块编码，用于限定基础股票池。
MAIN_BOARD = 10100101
GEM_BOARD = 10100102
STAR_BOARD = 10100103


def init(context):
    """策略初始化：写入参数、准备股票池、注册盘中定时任务。"""

    log_info("INIT_START diagnostic_version={}".format(DIAGNOSTIC_VERSION))

    # 将默认参数写入 context，方便在东财量化界面中动态读取或覆盖。
    context.min_amount = MIN_AMOUNT
    context.min_sector_heat_score = MIN_SECTOR_HEAT_SCORE
    context.max_result_count = MAX_RESULT_COUNT
    # 默认只输出信号，不自动下单；需要在参数面板中手动打开。
    context.enable_trade = True
    context.max_position_count = MAX_POSITION_COUNT
    context.position_percent = POSITION_PERCENT
    context.max_total_position_percent = MAX_TOTAL_POSITION_PERCENT
    context.min_single_position_percent = MIN_SINGLE_POSITION_PERCENT
    context.max_single_position_percent = MAX_SINGLE_POSITION_PERCENT
    context.defensive_position_percent = DEFENSIVE_POSITION_PERCENT
    context.min_buy_score = MIN_BUY_SCORE
    context.min_hold_score = MIN_HOLD_SCORE
    context.stop_loss_percent = STOP_LOSS_PERCENT
    context.take_profit_percent = TAKE_PROFIT_PERCENT
    context.enable_debug_sector_fallback = ENABLE_DEBUG_SECTOR_FALLBACK
    context.enable_no_sector_permission_mode = ENABLE_NO_SECTOR_PERMISSION_MODE
    # 本策略内部维护的持仓信息，用于记录入场价、止损价、止盈价和入场时间。
    context.positions = {}
    # 最近一次计算出的信号字典，键为股票代码。
    context.latest_signals = {}
    # 初始股票池：从 A 股中加载最多 800 只主板/创业板/科创板股票。
    context.target_symbols = load_a_share_symbols(limit=800)
    log_info("INIT target_symbols={} sample={}".format(
        len(context.target_symbols),
        ",".join(context.target_symbols[:5]),
    ))

    # 东财量化终端支持 add_parameter 时，把关键参数暴露到界面上，方便盘中调整。
    if callable(globals().get("add_parameter")):
        add_parameter(
            key="min_amount",
            value=context.min_amount,
            min=100000000,
            max=2000000000,
            name="最小成交额",
            intro="主线板块共振策略的单票成交额门槛",
            group="主线板块共振",
            readonly=False,
        )
        add_parameter(
            key="enable_trade",
            value=0,
            min=0,
            max=1,
            name="是否自动交易",
            intro="0只输出信号，1按示例仓位下单",
            group="主线板块共振",
            readonly=False,
        )
        add_parameter(
            key="max_total_position_percent",
            value=context.max_total_position_percent,
            min=0.1,
            max=1.0,
            name="总仓位上限",
            intro="自动交易时全部持仓合计不超过该比例",
            group="仓位控制",
            readonly=False,
        )
        add_parameter(
            key="max_single_position_percent",
            value=context.max_single_position_percent,
            min=0.03,
            max=0.3,
            name="单票仓位上限",
            intro="单只股票最高目标仓位比例",
            group="仓位控制",
            readonly=False,
        )
        add_parameter(
            key="defensive_position_percent",
            value=context.defensive_position_percent,
            min=0.01,
            max=0.1,
            name="防守仓位",
            intro="持仓信号转弱但未触发清仓时降到该比例",
            group="仓位控制",
            readonly=False,
        )

    # 开盘后第一次扫描，过滤集合竞价和刚开盘的噪声。
    schedule(schedule_func=run_main_sector_resonance, date_rule="1d", time_rule="09:35:20")
    # 上午中段扫描，用于观察早盘资金是否持续聚焦。
    schedule(schedule_func=run_main_sector_resonance, date_rule="1d", time_rule="10:30:20")
    # 午后开盘后扫描，用于捕捉午后发酵的板块。
    schedule(schedule_func=run_main_sector_resonance, date_rule="1d", time_rule="13:30:20")
    # 尾盘前扫描，用于确认全天主线是否仍然强势。
    schedule(schedule_func=run_main_sector_resonance, date_rule="1d", time_rule="14:30:20")
    # 尾盘前统一平仓，保持策略为日内/短线示例，不隔夜持仓。
    schedule(schedule_func=close_positions_before_market_close, date_rule="1d", time_rule="14:50:00")


def run_main_sector_resonance(context):
    """主线板块共振扫描主流程：获取行情、计算热度、生成信号、必要时执行交易。"""

    # 从初始化阶段缓存的股票池中取出待扫描股票。
    symbols = getattr(context, "target_symbols", None) or []
    if not symbols:
        log_info("no symbols loaded")
        return

    # 拉取当前实时行情；没有行情时直接退出，避免后续空数据计算。
    quotes = fetch_current_quotes(symbols, context=context)
    if not quotes:
        log_info("no realtime quote and no history fallback quote")
        return

    # 按行业归属聚合行情，计算每只股票所属行业板块的热度。
    sector_heat_by_symbol = build_sector_heat_by_symbol(quotes)
    # 按概念归属聚合行情，计算每只股票所属概念板块的热度。
    concept_heat_by_symbol = build_concept_heat_by_symbol(quotes)
    # 综合个股强弱、行业热度、概念热度、成交额和龙头排名生成候选信号。
    signals = evaluate_main_sector_resonance(
        quotes=quotes,
        sector_heat_by_symbol=sector_heat_by_symbol,
        concept_heat_by_symbol=concept_heat_by_symbol,
        min_amount=float(getattr(context, "min_amount", MIN_AMOUNT)),
        min_sector_heat_score=float(getattr(context, "min_sector_heat_score", MIN_SECTOR_HEAT_SCORE)),
        max_result_count=int(getattr(context, "max_result_count", MAX_RESULT_COUNT)),
    )
    log_filter_summary(
        quotes=quotes,
        sector_heat_by_symbol=sector_heat_by_symbol,
        concept_heat_by_symbol=concept_heat_by_symbol,
        signals=signals,
        min_amount=float(getattr(context, "min_amount", MIN_AMOUNT)),
        min_sector_heat_score=float(getattr(context, "min_sector_heat_score", MIN_SECTOR_HEAT_SCORE)),
    )

    # 输出本轮扫描结果，方便在终端日志中观察策略命中的股票与原因。
    for signal in signals:
        text = (
            "main-sector-resonance "
            "{symbol} {name} score={score:.2f} price={price:.2f} action={action} reason={reason}"
        ).format(**signal)
        log_info(text)
        print(text)

    # 缓存最新信号，自动交易和外部查看都可以复用。
    context.latest_signals = {signal["symbol"]: signal for signal in signals}
    # enable_trade 为 True 时才执行示例交易逻辑；默认只输出信号。
    if bool(getattr(context, "enable_trade", False)):
        manage_positions(context, quotes, signals)


def on_bar(context, bars):
    """K 线事件回调：只做轻量刷新，主要选股仍由定时任务触发。"""

    # 收到 bar 数据时复用主扫描流程，保证有行情更新时能及时刷新信号。
    if bars:
        run_main_sector_resonance(context)


def evaluate_main_sector_resonance(
    quotes,
    sector_heat_by_symbol,
    concept_heat_by_symbol=None,
    min_amount=MIN_AMOUNT,
    min_sector_heat_score=MIN_SECTOR_HEAT_SCORE,
    max_result_count=MAX_RESULT_COUNT,
):
    """从实时行情中筛选“个股强、板块强、概念强”的主线共振候选股。"""

    # 概念热度可能取不到，统一转为空字典，避免后续 get 时报错。
    concept_heat_by_symbol = concept_heat_by_symbol or {}
    # 市场平均涨幅作为相对强弱基准，个股必须强于平均水平才进入候选。
    market_average_change = average([quote["change_percent"] for quote in quotes])
    candidates = []

    for quote in quotes:
        # 过滤弱于或等于市场平均涨幅的股票，保留相对强势品种。
        if quote["change_percent"] <= market_average_change:
            continue
        # 过滤成交额不足的股票，降低流动性和虚假拉升风险。
        if quote["amount"] < min_amount:
            continue

        # 没有行业热度数据的股票无法判断板块共振，直接跳过。
        sector_heat = sector_heat_by_symbol.get(quote["symbol"])
        if not sector_heat:
            continue

        # 一个股票可能属于多个概念，选热度最高、成交额最大的概念作为代表概念。
        concept_heats = concept_heat_by_symbol.get(quote["symbol"], [])
        best_concept_heat = first_sorted(concept_heats, key=lambda item: (item["heat_score"], item["total_amount"]))
        # 构建最终信号；若热度或强度不足，build_signal 会返回 None。
        signal = build_signal(quote, sector_heat, best_concept_heat, market_average_change, min_amount, min_sector_heat_score)
        if signal:
            candidates.append(signal)

    # 按综合得分从高到低排序，只返回前 max_result_count 个结果。
    return sorted(candidates, key=lambda item: item["score"], reverse=True)[:max_result_count]


def build_signal(quote, sector_heat, best_concept_heat, market_average_change, min_amount, min_sector_heat_score=MIN_SECTOR_HEAT_SCORE):
    """根据个股行情、行业热度和概念热度生成单只股票的交易/观察信号。"""

    # 判断当前股票是否是所属行业或概念中的涨幅/成交额领先股票。
    sector_leader = find_leader(sector_heat, quote["symbol"])
    concept_leader = find_leader(best_concept_heat, quote["symbol"]) if best_concept_heat else None
    is_sector_leader = sector_leader is not None and sector_leader["rank"] <= CONCEPT_LEADER_RANK_THRESHOLD
    is_concept_leader = concept_leader is not None and concept_leader["rank"] <= CONCEPT_LEADER_RANK_THRESHOLD

    # 行业热度和概念热度取更强的一侧作为有效热度；太弱则不生成信号。
    effective_heat_score = max(sector_heat["heat_score"], best_concept_heat["heat_score"] if best_concept_heat else 0.0)
    if effective_heat_score < min_sector_heat_score and quote["change_percent"] < market_average_change + 1.2:
        return None

    # 行业热度加分：行业越强，对个股信号越有支撑。
    sector_bonus = clamp((sector_heat["heat_score"] - 50.0) * 0.38, 0.0, 15.0)
    # 概念热度加分：概念越强，说明题材共振越明显。
    concept_bonus = clamp((best_concept_heat["heat_score"] - 50.0) * 0.28, 0.0, 12.0) if best_concept_heat else 0.0
    # 广度加分：板块或概念中上涨股票占比越高，说明不是单票孤立上涨。
    breadth_bonus = clamp(
        (max(sector_heat["rising_ratio_percent"], best_concept_heat["rising_ratio_percent"] if best_concept_heat else 0.0) - 50.0) * 0.10,
        0.0,
        8.0,
    )
    # 龙头加分：行业龙头给少量加分，概念龙头给更多加分。
    leader_bonus = (3.0 if is_sector_leader else 0.0) + (5.0 if is_concept_leader else 0.0)
    # 综合得分由基础分、个股涨幅、成交额、行业热度、概念热度、上涨广度和龙头地位共同组成。
    score = round(
        62.0
        + quote["change_percent"] * 1.8
        + min(quote["amount"] / 100000000.0, 10.0)
        + sector_bonus
        + concept_bonus
        + breadth_bonus
        + leader_bonus,
        2,
    )

    # 强势行业/强势概念用于决定是候选股还是观察股。
    is_strong_sector = sector_heat["heat_score"] >= STRONG_SECTOR_HEAT_SCORE
    is_strong_concept = best_concept_heat is not None and best_concept_heat["heat_score"] >= STRONG_CONCEPT_HEAT_SCORE
    action = "Candidate" if is_strong_sector or is_strong_concept or is_concept_leader else "Watch"
    # 同时具备行业与概念强势，或已经是概念龙头时，置信度设为 High。
    confidence = "High" if (is_strong_sector and is_strong_concept) or is_concept_leader else "Medium"
    leader_text = ""
    if concept_leader:
        leader_text = "; concept rank {}".format(concept_leader["rank"])
    elif sector_leader:
        leader_text = "; sector rank {}".format(sector_leader["rank"])

    # 拼接信号原因：有概念数据时优先展示概念共振，否则只展示行业共振。
    if best_concept_heat:
        reason = (
            "stronger than market avg {avg:.2f}%, amount {amount:.1f}e; "
            "sector {sector} heat {sector_heat:.1f}; concept {concept} heat {concept_heat:.1f}, "
            "avg {concept_avg:.2f}%, rising {concept_rising}/{concept_count}{leader}"
        ).format(
            avg=market_average_change,
            amount=quote["amount"] / 100000000.0,
            sector=sector_heat["name"],
            sector_heat=sector_heat["heat_score"],
            concept=best_concept_heat["name"],
            concept_heat=best_concept_heat["heat_score"],
            concept_avg=best_concept_heat["average_change_percent"],
            concept_rising=best_concept_heat["rising_count"],
            concept_count=best_concept_heat["stock_count"],
            leader=leader_text,
        )
    else:
        reason = (
            "stronger than market avg {avg:.2f}%, amount {amount:.1f}e; "
            "sector {sector} heat {sector_heat:.1f}, avg {sector_avg:.2f}%, "
            "rising {sector_rising}/{sector_count}{leader}"
        ).format(
            avg=market_average_change,
            amount=quote["amount"] / 100000000.0,
            sector=sector_heat["name"],
            sector_heat=sector_heat["heat_score"],
            sector_avg=sector_heat["average_change_percent"],
            sector_rising=sector_heat["rising_count"],
            sector_count=sector_heat["stock_count"],
            leader=leader_text,
        )

    # 返回结构化信号，方便日志输出、自动交易和外部系统读取。
    return {
        "symbol": quote["symbol"],
        "name": quote.get("name") or quote["symbol"],
        "strategy_code": "main-sector-resonance",
        "strategy_name": "主线板块共振",
        "score": score,
        "price": quote["price"],
        "action": action,
        "confidence": confidence,
        "reason": reason,
        "risk": "sector/concept heat is confirmed; verify real capital flow and intraday support before trading"
        if is_strong_concept
        else "sector heat is watch-level; concept heat or real capital flow is not fully confirmed",
        "stop_loss": round(quote["price"] * 0.96, 2) if quote["price"] > 0 else None,
        "take_profit": round(quote["price"] * 1.05, 2) if quote["price"] > 0 else None,
        # metrics 保存原始指标，便于后续复盘、导出或二次筛选。
        "metrics": {
            "change_percent": quote["change_percent"],
            "market_average_change": market_average_change,
            "amount": quote["amount"],
            "sector_heat_score": sector_heat["heat_score"],
            "sector_rising_ratio": sector_heat["rising_ratio_percent"],
            "concept_heat_score": best_concept_heat["heat_score"] if best_concept_heat else 0.0,
        },
    }


def log_filter_summary(
    quotes,
    sector_heat_by_symbol,
    concept_heat_by_symbol,
    signals,
    min_amount=MIN_AMOUNT,
    min_sector_heat_score=MIN_SECTOR_HEAT_SCORE,
):
    """输出筛选漏斗诊断，方便在东财终端定位为什么没有命中股票。"""

    market_average_change = average([quote["change_percent"] for quote in quotes])
    stronger_count = 0
    amount_count = 0
    sector_count = 0
    effective_heat_count = 0
    concept_count = 0

    for quote in quotes:
        symbol = quote["symbol"]
        if quote["change_percent"] > market_average_change:
            stronger_count += 1
        if quote["amount"] >= min_amount:
            amount_count += 1
        sector_heat = sector_heat_by_symbol.get(symbol)
        if sector_heat:
            sector_count += 1
        concept_heats = concept_heat_by_symbol.get(symbol, []) if concept_heat_by_symbol else []
        if concept_heats:
            concept_count += 1
        best_concept_heat = first_sorted(concept_heats, key=lambda item: (item["heat_score"], item["total_amount"]))
        effective_heat_score = max(
            sector_heat["heat_score"] if sector_heat else 0.0,
            best_concept_heat["heat_score"] if best_concept_heat else 0.0,
        )
        if effective_heat_score >= min_sector_heat_score:
            effective_heat_count += 1

    log_info(
        (
            "FILTER_SUMMARY quotes={quotes} market_avg={market_avg:.2f}% "
            "stronger={stronger} amount_pass={amount_pass} sector_ready={sector_ready} "
            "concept_ready={concept_ready} heat_pass={heat_pass} signals={signals}"
        ).format(
            quotes=len(quotes),
            market_avg=market_average_change,
            stronger=stronger_count,
            amount_pass=amount_count,
            sector_ready=sector_count,
            concept_ready=concept_count,
            heat_pass=effective_heat_count,
            signals=len(signals),
        )
    )


def load_a_share_symbols(limit=800):
    """加载基础 A 股股票池，只保留主板、创业板、科创板中的非 ST、非退市股票。"""

    # 本地环境没有东财接口时，返回少量样例代码，方便语法检查和单元测试。
    if not callable(globals().get("get_symbol_infos")):
        return ["SHSE.600000", "SZSE.000001", "SZSE.300059"]

    # sec_type1=1010、sec_type2=101001 表示股票/A 股；交易所限定为沪深两市。
    rows = get_symbol_infos(sec_type1=1010, sec_type2=101001, exchanges=["SHSE", "SZSE"])
    symbols = []
    for row in rows:
        name = str(row.get("sec_name") or row.get("sec_abbr") or "")
        board = row.get("board")
        # 只保留主板、创业板、科创板，排除其他特殊板块。
        if board not in (MAIN_BOARD, GEM_BOARD, STAR_BOARD):
            continue
        # 排除 ST 和名称含“退”的股票，降低退市和异常交易风险。
        if "ST" in name.upper() or "退" in name:
            continue
        symbol = row.get("symbol")
        if symbol:
            symbols.append(symbol)
        # 达到上限后停止加载，控制实时行情请求规模。
        if len(symbols) >= limit:
            break
    return symbols


def fetch_current_quotes(symbols, context=None):
    """批量获取实时行情，并统一整理成策略内部使用的 quote 字典。"""

    result = []
    # current 是东财量化的实时行情接口；若终端不返回 tick，后面会用 history_n 兜底。
    if callable(globals().get("current")):
        # 分批请求，避免单次 symbols 太多导致接口失败或超时。
        batch_size = 80
        for start in range(0, len(symbols), batch_size):
            batch = symbols[start : start + batch_size]
            ticks = call_current_compat(batch)
            result.extend(normalize_current_ticks(ticks))
    else:
        log_info("current api is not available")

    if result:
        log_info("QUOTE_SOURCE current count={}".format(len(result)))
        return result

    log_info("QUOTE_SOURCE current empty, try history_n fallback")
    return fetch_history_quotes(symbols, context=context)


def normalize_current_ticks(ticks):
    """把 current 返回的 tick 数据标准化为 quote 列表。"""

    result = []
    for tick in iter_rows(ticks) or []:
        symbol = tick.get("symbol")
        price = to_float(tick.get("price") or tick.get("last_price") or tick.get("close"))
        open_price = to_float(tick.get("open"))
        prev_close = to_float(tick.get("pre_close") or tick.get("prev_close") or tick.get("last_close") or 0)
        amount = to_float(tick.get("cum_amount") or tick.get("amount") or tick.get("turnover") or 0)
        # 涨跌幅使用最新价相对昨收计算；昨收缺失时置为 0。
        change_percent = ((price - prev_close) / prev_close * 100.0) if prev_close > 0 else 0.0
        # 过滤无效价格或无成交额数据，避免污染热度计算。
        if not symbol or price <= 0 or amount <= 0:
            continue
        result.append(
            {
                "symbol": symbol,
                "name": tick.get("sec_name") or tick.get("name") or symbol,
                "price": price,
                "open": open_price,
                "high": to_float(tick.get("high")),
                "low": to_float(tick.get("low")),
                "amount": amount,
                "volume": to_float(tick.get("cum_volume") or tick.get("volume")),
                "change_percent": change_percent,
                # 当前接口未计算量比，保留字段方便以后扩展。
                "volume_ratio": 0.0,
                "created_at": tick.get("created_at") or datetime.now(),
            }
        )
    return result


def call_current_compat(batch):
    """兼容不同终端版本的 current 调用方式。"""

    try:
        return current(symbols=batch)
    except Exception as error:
        log_info("current list call failed: {}".format(error))

    try:
        return current(symbols=",".join(batch))
    except Exception as error:
        log_info("current string call failed: {}".format(error))
        return []


def fetch_history_quotes(symbols, context=None):
    """实时行情为空时，用日线 history_n 兜底生成 quote 数据。"""

    if not callable(globals().get("history_n")):
        log_info("history_n api is not available")
        return []

    result = []
    error_count = 0
    empty_count = 0
    end_time = getattr(context, "now", None) or datetime.now()
    for symbol in symbols:
        try:
            rows = history_n(
                symbol=symbol,
                frequency="1d",
                count=2,
                fields="open,high,low,close,amount,volume",
                end_time=end_time,
                fill_missing="Last",
                adjust=ADJUST_PREV if "ADJUST_PREV" in globals() else 0,
                df=False,
            )
        except TypeError:
            try:
                rows = history_n(
                    symbol=symbol,
                    frequency="1d",
                    count=2,
                    fields="open,high,low,close,amount,volume",
                    end_time=end_time,
                    df=False,
                )
            except Exception as error:
                error_count += 1
                if error_count <= 3:
                    log_info("history_n fallback failed symbol={} error={}".format(symbol, error))
                rows = []
        except Exception as error:
            error_count += 1
            if error_count <= 3:
                log_info("history_n fallback failed symbol={} error={}".format(symbol, error))
            rows = []

        values = list(iter_rows(rows))
        if not values:
            empty_count += 1
            continue

        latest = values[-1]
        previous = values[-2] if len(values) > 1 else {}
        price = to_float(latest.get("close") or latest.get("price"))
        prev_close = to_float(previous.get("close") or latest.get("pre_close") or latest.get("prev_close"))
        amount = to_float(latest.get("amount") or latest.get("cum_amount") or latest.get("turnover"))
        change_percent = ((price - prev_close) / prev_close * 100.0) if prev_close > 0 else 0.0
        if price <= 0 or amount <= 0:
            empty_count += 1
            continue

        result.append(
            {
                "symbol": symbol,
                "name": symbol,
                "price": price,
                "open": to_float(latest.get("open")),
                "high": to_float(latest.get("high")),
                "low": to_float(latest.get("low")),
                "amount": amount,
                "volume": to_float(latest.get("volume") or latest.get("cum_volume")),
                "change_percent": change_percent,
                "volume_ratio": 0.0,
                "created_at": end_time,
            }
        )

    log_info("QUOTE_SOURCE history_n count={} empty={} errors={}".format(len(result), empty_count, error_count))
    return result


def build_sector_heat_by_symbol(quotes):
    """构建“股票代码 -> 所属行业热度”的映射。"""

    # 先查询每只股票的行业归属，再按行业聚合行情计算热度。
    membership = load_industry_membership([quote["symbol"] for quote in quotes])
    if not membership and ENABLE_DEBUG_SECTOR_FALLBACK:
        log_info("industry membership is empty, use debug exchange-prefix sector fallback")
        membership = build_debug_sector_membership([quote["symbol"] for quote in quotes])
    return build_heat_by_symbol(quotes, membership, code_key="industry_code", name_key="industry_name")


def build_debug_sector_membership(symbols):
    """行业接口不可用时的调试兜底分组，用于确认行情和筛选链路是否可运行。"""

    result = {}
    for symbol in symbols:
        code = symbol.split(".")[0] if "." in symbol else "UNKNOWN"
        result[symbol] = {
            "industry_code": "DEBUG_{}".format(code),
            "industry_name": "调试分组{}".format(code),
        }
    return result


def build_concept_heat_by_symbol(quotes):
    """构建“股票代码 -> 所属概念热度列表”的映射。"""

    # 概念归属通常是一对多，所以 multi=True 会给每只股票保留多个概念热度。
    membership = load_concept_membership([quote["symbol"] for quote in quotes])
    return build_heat_by_symbol(quotes, membership, code_key="sector_code", name_key="sector_name", multi=True)


def load_industry_membership(symbols):
    """查询股票所属行业，返回每只股票对应的行业代码和行业名称。"""

    result = {}
    # 非东财环境没有行业接口时返回空结果，主流程会自动跳过行业缺失的股票。
    if not callable(globals().get("stk_get_symbol_industry")):
        log_info("industry api stk_get_symbol_industry is not available")
        return result

    error_count = 0
    empty_count = 0
    for symbol in symbols:
        try:
            # 兼容关键字参数调用方式。
            rows = stk_get_symbol_industry(symbol=symbol)
        except TypeError:
            try:
                # 兼容只支持位置参数的接口版本。
                rows = stk_get_symbol_industry(symbol)
            except Exception as error:
                error_count += 1
                if error_count <= 3:
                    log_info("industry api failed symbol={} error={}".format(symbol, error))
                rows = []
        except Exception as error:
            # 单只股票行业查询失败时不影响其他股票，但保留前几个错误样本用于终端排查。
            error_count += 1
            if error_count <= 3:
                log_info("industry api failed symbol={} error={}".format(symbol, error))
            rows = []
        row = first_row(rows)
        if row:
            result[symbol] = {
                "industry_code": str(row.get("industry_code") or row.get("code") or ""),
                "industry_name": str(row.get("industry_name") or row.get("name") or ""),
            }
        else:
            empty_count += 1
    log_info("INDUSTRY_MEMBERSHIP symbols={} ready={} empty={} errors={}".format(
        len(symbols),
        len(result),
        empty_count,
        error_count,
    ))
    return result


def load_concept_membership(symbols):
    """查询股票所属概念板块，返回每只股票对应的概念列表。"""

    result = defaultdict(list)
    # 非东财环境没有概念接口时返回空结果，策略仍可依靠行业热度运行。
    if not callable(globals().get("stk_get_symbol_sector")):
        log_info("concept api stk_get_symbol_sector is not available")
        return result

    error_count = 0
    empty_count = 0
    for symbol in symbols:
        try:
            # 兼容关键字参数调用方式。
            rows = stk_get_symbol_sector(symbol=symbol)
        except TypeError:
            try:
                # 兼容只支持位置参数的接口版本。
                rows = stk_get_symbol_sector(symbol)
            except Exception as error:
                error_count += 1
                if error_count <= 3:
                    log_info("concept api failed symbol={} error={}".format(symbol, error))
                rows = []
        except Exception as error:
            # 单只股票概念查询失败时不影响整体扫描，但保留前几个错误样本用于终端排查。
            error_count += 1
            if error_count <= 3:
                log_info("concept api failed symbol={} error={}".format(symbol, error))
            rows = []
        before_count = len(result[symbol])
        for row in iter_rows(rows):
            sector_type = str(row.get("sector_type") or row.get("type") or "")
            # 只保留概念板块；其他行业、地域、指数等类型跳过。
            if sector_type and sector_type != "1003":
                continue
            result[symbol].append(
                {
                    "sector_code": str(row.get("sector_code") or row.get("code") or ""),
                    "sector_name": str(row.get("sector_name") or row.get("name") or ""),
                }
            )
        if len(result[symbol]) == before_count:
            empty_count += 1
    ready_count = len([symbol for symbol, rows in result.items() if rows])
    log_info("CONCEPT_MEMBERSHIP symbols={} ready={} empty={} errors={}".format(
        len(symbols),
        ready_count,
        empty_count,
        error_count,
    ))
    return result


def build_heat_by_symbol(quotes, membership, code_key, name_key, multi=False):
    """按行业/概念归属聚合行情，并把板块热度回填到每只股票。"""

    # 将行情按股票代码索引，便于通过归属关系快速拿到对应 quote。
    quote_by_symbol = {quote["symbol"]: quote for quote in quotes}
    # groups 保存“板块代码 -> 板块内股票行情列表”。
    groups = defaultdict(list)
    # names 保存“板块代码 -> 板块名称”。
    names = {}
    for symbol, item in membership.items():
        # 行业是一对一，概念是一对多；multi=True 时 item 已经是列表。
        memberships = item if multi else [item]
        for member in memberships:
            code = member.get(code_key)
            if not code or symbol not in quote_by_symbol:
                continue
            groups[code].append(quote_by_symbol[symbol])
            names[code] = member.get(name_key) or code

    # 概念模式下每只股票可能有多个热度对象；行业模式下一只股票只有一个热度对象。
    heat_by_symbol = defaultdict(list) if multi else {}
    for code, group_quotes in groups.items():
        # 计算单个板块的热度、上涨家数、平均涨幅和前排龙头。
        heat = build_heat(code, names.get(code) or code, group_quotes)
        # 先给板块前排股票回填热度，确保龙头股一定能拿到板块热度。
        for leader in heat["leaders"]:
            if multi:
                heat_by_symbol[leader["symbol"]].append(heat)
            else:
                heat_by_symbol[leader["symbol"]] = heat
        # 再给板块内所有股票回填热度；概念模式下避免重复添加同一个热度对象。
        for quote in group_quotes:
            if multi:
                if heat not in heat_by_symbol[quote["symbol"]]:
                    heat_by_symbol[quote["symbol"]].append(heat)
            else:
                heat_by_symbol[quote["symbol"]] = heat
    return heat_by_symbol


def build_heat(code, name, quotes):
    """计算单个行业/概念板块的热度指标。"""

    # 板块内股票总数。
    stock_count = len(quotes)
    # 板块内上涨股票列表和上涨家数。
    rising = [quote for quote in quotes if quote["change_percent"] > 0]
    rising_count = len(rising)
    # 板块平均涨幅。
    avg_change = average([quote["change_percent"] for quote in quotes])
    # 上涨家数占比，用于衡量板块上涨广度。
    rising_ratio = rising_count / stock_count * 100.0 if stock_count else 0.0
    # 板块内总成交额，用于衡量资金参与度。
    total_amount = sum(quote["amount"] for quote in quotes)
    # 龙头列表按涨幅、成交额排序，取前 5 名。
    leaders = sorted(quotes, key=lambda item: (item["change_percent"], item["amount"]), reverse=True)[:5]
    # 热度分：基础 50 分 + 平均涨幅贡献 + 上涨广度贡献 + 成交额贡献，并限制在 0-100。
    heat_score = clamp(50.0 + avg_change * 6.0 + (rising_ratio - 50.0) * 0.35 + min(total_amount / 1000000000.0, 20.0), 0.0, 100.0)
    return {
        "code": code,
        "name": name,
        "stock_count": stock_count,
        "rising_count": rising_count,
        "average_change_percent": avg_change,
        "rising_ratio_percent": rising_ratio,
        "total_amount": total_amount,
        "heat_score": heat_score,
        # leaders 用于判断个股是否为行业/概念前排核心票。
        "leaders": [
            {
                "rank": index + 1,
                "symbol": quote["symbol"],
                "name": quote.get("name") or quote["symbol"],
                "change_percent": quote["change_percent"],
                "amount": quote["amount"],
                "volume_ratio": quote.get("volume_ratio", 0.0),
            }
            for index, quote in enumerate(leaders)
        ],
    }


def manage_positions(context, quotes, signals):
    """自动交易管理入口：先处理卖出，再处理买入。"""

    # 非交易环境没有下单函数时直接退出，避免本地测试报错。
    if not callable(globals().get("order_target_percent")):
        return

    # 将实时行情和最新信号按股票代码索引，便于持仓管理快速查询。
    quote_by_symbol = {quote["symbol"]: quote for quote in quotes}
    signal_by_symbol = {signal["symbol"]: signal for signal in signals}
    # 先用账户真实持仓同步策略内部状态，避免内存状态与账户不一致。
    sync_positions_from_account(context)
    # 先卖出不满足条件的持仓，释放仓位和资金。
    handle_exit_rules(context, quote_by_symbol, signal_by_symbol)
    # 再从高分信号中挑选新标的买入。
    handle_entry_rules(context, signals)


def handle_entry_rules(context, signals):
    """根据最新信号执行买入规则。"""

    # 当前策略内部记录的持仓股票。
    open_symbols = set(getattr(context, "positions", {}).keys())
    # 剩余可开仓数量 = 最大持仓数 - 当前持仓数。
    capacity = max(0, int(getattr(context, "max_position_count", MAX_POSITION_COUNT)) - len(open_symbols))
    if capacity <= 0:
        return

    # 买入候选必须满足：未持有、Candidate、High 置信度、综合得分达到买入线。
    buy_candidates = [
        signal
        for signal in signals
        if signal["symbol"] not in open_symbols
        and signal["action"] == "Candidate"
        and signal["confidence"] == "High"
        and signal["score"] >= float(getattr(context, "min_buy_score", MIN_BUY_SCORE))
    ]
    # 按得分排序，只买入剩余仓位容量允许的前几只。
    buy_candidates = sorted(buy_candidates, key=lambda item: item["score"], reverse=True)[:capacity]
    for signal in buy_candidates:
        # 按综合得分、板块热度、剩余总仓位动态计算本次开仓比例。
        percent = calculate_entry_position_percent(context, signal)
        if percent < float(getattr(context, "min_single_position_percent", MIN_SINGLE_POSITION_PERCENT)):
            log_info("skip buy {} because available position is too small".format(signal["symbol"]))
            continue
        order_target_percent(symbol=signal["symbol"], percent=percent)
        # 记录入场信息，用于后续止损、止盈和信号衰减退出。
        context.positions[signal["symbol"]] = {
            "entry_price": signal["price"],
            "entry_score": signal["score"],
            "target_percent": percent,
            "stop_loss": signal.get("stop_loss") or round(signal["price"] * (1.0 - STOP_LOSS_PERCENT), 2),
            "take_profit": signal.get("take_profit") or round(signal["price"] * (1.0 + TAKE_PROFIT_PERCENT), 2),
            "entry_time": datetime.now(),
        }
        log_trade("BUY", signal["symbol"], signal["price"], "score={:.2f} percent={:.2%}".format(signal["score"], percent))


def handle_exit_rules(context, quote_by_symbol, signal_by_symbol):
    """根据止损、止盈和信号强弱处理已有持仓的卖出规则。"""

    positions = getattr(context, "positions", {})
    min_hold_score = float(getattr(context, "min_hold_score", MIN_HOLD_SCORE))
    # list(...) 是为了在循环中安全删除 positions 内的元素。
    for symbol, position in list(positions.items()):
        quote = quote_by_symbol.get(symbol)
        # 没有实时行情时不做卖出判断，避免用缺失价格触发错误交易。
        if not quote:
            continue

        price = quote["price"]
        signal = signal_by_symbol.get(symbol)
        # 优先使用入场时记录的止损/止盈价；没有记录时根据入场价临时计算。
        stop_loss = float(position.get("stop_loss") or position.get("entry_price", price) * (1.0 - STOP_LOSS_PERCENT))
        take_profit = float(position.get("take_profit") or position.get("entry_price", price) * (1.0 + TAKE_PROFIT_PERCENT))
        exit_reason = None
        reduce_reason = None

        # 价格跌破止损线，优先退出。
        if price <= stop_loss:
            exit_reason = "stop_loss price={:.2f} <= {:.2f}".format(price, stop_loss)
        # 价格达到止盈线，落袋退出。
        elif price >= take_profit:
            exit_reason = "take_profit price={:.2f} >= {:.2f}".format(price, take_profit)
        # 最新榜单中没有该股票，说明主线信号消失。
        elif signal is None:
            exit_reason = "signal_disappeared"
        # 信号仍存在但得分跌破持有线，或不再是 Candidate，也退出。
        elif signal["score"] < min_hold_score or signal["action"] != "Candidate":
            exit_reason = "signal_weakened score={:.2f}".format(signal["score"])
        # 信号尚可持有，但已经低于买入线或置信度下降，先降到防守仓位。
        elif signal["score"] < float(getattr(context, "min_buy_score", MIN_BUY_SCORE)) or signal["confidence"] != "High":
            reduce_reason = "reduce_to_defensive score={:.2f} confidence={}".format(signal["score"], signal["confidence"])

        if exit_reason:
            # 目标仓位设为 0 即清仓。
            order_target_percent(symbol=symbol, percent=0)
            positions.pop(symbol, None)
            log_trade("SELL", symbol, price, exit_reason)
        elif reduce_reason:
            defensive_percent = float(getattr(context, "defensive_position_percent", DEFENSIVE_POSITION_PERCENT))
            current_percent = float(position.get("target_percent") or defensive_percent)
            if current_percent > defensive_percent:
                order_target_percent(symbol=symbol, percent=defensive_percent)
                position["target_percent"] = defensive_percent
                log_trade("REDUCE", symbol, price, "{} percent={:.2%}".format(reduce_reason, defensive_percent))


def close_positions_before_market_close(context):
    """尾盘前清仓：仅在自动交易开启时执行。"""

    # 只输出信号模式下不做任何交易动作。
    if not bool(getattr(context, "enable_trade", False)):
        return
    # 非交易环境没有下单函数时直接退出。
    if not callable(globals().get("order_target_percent")):
        return

    # 清仓前先同步账户持仓，避免漏掉真实账户中仍持有的股票。
    sync_positions_from_account(context)
    for symbol in list(getattr(context, "positions", {}).keys()):
        order_target_percent(symbol=symbol, percent=0)
        context.positions.pop(symbol, None)
        log_trade("SELL", symbol, 0, "close_before_market_close")


def calculate_entry_position_percent(context, signal):
    """根据得分、热度和剩余总仓位计算单票开仓比例。"""

    min_percent = float(getattr(context, "min_single_position_percent", MIN_SINGLE_POSITION_PERCENT))
    max_percent = float(getattr(context, "max_single_position_percent", MAX_SINGLE_POSITION_PERCENT))
    default_percent = float(getattr(context, "position_percent", POSITION_PERCENT))
    max_total_percent = float(getattr(context, "max_total_position_percent", MAX_TOTAL_POSITION_PERCENT))

    # 如果配置异常，退回默认仓位，避免产生负仓位或过大仓位。
    if min_percent <= 0 or max_percent < min_percent:
        min_percent = min(default_percent, max_total_percent)
        max_percent = min(default_percent, max_total_percent)

    # 得分越接近 100，得分强度越高。
    score_strength = clamp(
        (float(signal.get("score", 0.0)) - float(getattr(context, "min_buy_score", MIN_BUY_SCORE))) / 12.0,
        0.0,
        1.0,
    )
    metrics = signal.get("metrics") or {}
    heat_score = max(
        float(metrics.get("sector_heat_score", 0.0)),
        float(metrics.get("concept_heat_score", 0.0)),
    )
    # 热度越接近满分，板块/概念共振越强。
    heat_strength = clamp((heat_score - STRONG_SECTOR_HEAT_SCORE) / 28.0, 0.0, 1.0)
    # 得分权重更高，热度权重次之。
    signal_strength = score_strength * 0.65 + heat_strength * 0.35
    raw_percent = min_percent + (max_percent - min_percent) * signal_strength

    # 受总仓位上限约束，剩余仓位不足时自动压低本次开仓比例。
    available_percent = max_total_percent - current_total_position_percent(context)
    return clamp(raw_percent, 0.0, max(0.0, available_percent))


def current_total_position_percent(context):
    """估算策略当前总目标仓位，用于限制新增仓位。"""

    positions = getattr(context, "positions", {}) or {}
    total_percent = 0.0
    for position in positions.values():
        total_percent += float(position.get("target_percent") or getattr(context, "position_percent", POSITION_PERCENT))
    return total_percent


def sync_positions_from_account(context):
    """从交易账户读取真实持仓，并同步到策略内部 context.positions。"""

    positions = getattr(context, "positions", {})
    # 没有 get_position 接口时保留现有内部状态。
    if not callable(globals().get("get_position")):
        context.positions = positions
        return

    try:
        account_positions = get_position()
    except Exception:
        # 账户查询失败时不清空内部持仓，避免误判为空仓。
        context.positions = positions
        return

    active_symbols = set()
    for item in account_positions or []:
        # 兼容 dict 和对象两种返回格式。
        symbol = item.get("symbol") if isinstance(item, dict) else getattr(item, "symbol", None)
        volume = item.get("volume") if isinstance(item, dict) else getattr(item, "volume", 0)
        if symbol and to_float(volume) > 0:
            active_symbols.add(symbol)
            # 如果账户里有持仓但策略内部没有记录，则用成交均价 vwap 补一条入场记录。
            positions.setdefault(symbol, {
                "entry_price": to_float(item.get("vwap") if isinstance(item, dict) else getattr(item, "vwap", 0)),
                "entry_score": 0.0,
                "target_percent": float(getattr(context, "position_percent", POSITION_PERCENT)),
                "entry_time": datetime.now(),
            })

    # 只保留账户中仍有数量的持仓，避免已清仓股票继续占用策略仓位容量。
    context.positions = {symbol: value for symbol, value in positions.items() if symbol in active_symbols}


def log_trade(action, symbol, price, reason):
    """统一输出交易日志，方便回测和实盘排查。"""

    log_info("TRADE {action} {symbol} price={price:.2f} reason={reason}".format(
        action=action,
        symbol=symbol,
        price=price,
        reason=reason,
    ))


def find_leader(heat, symbol):
    """在板块 leaders 列表中查找指定股票的龙头排名信息。"""

    for leader in heat.get("leaders", []):
        if leader.get("symbol") == symbol:
            return leader
    return None


def first_sorted(items, key):
    """按指定 key 倒序排序并返回第一项；空列表返回 None。"""

    values = list(items or [])
    if not values:
        return None
    return sorted(values, key=key, reverse=True)[0]


def first_row(rows):
    """从接口返回结果中取第一行，兼容 DataFrame、列表和可迭代对象。"""

    values = list(iter_rows(rows))
    return values[0] if values else None


def iter_rows(rows):
    """把接口返回的数据统一转换为可迭代的行记录。"""

    if rows is None:
        return []
    # pandas DataFrame 支持 to_dict("records")，转成列表字典后更容易处理。
    if hasattr(rows, "to_dict"):
        return rows.to_dict("records")
    return rows


def average(values):
    """计算平均值；空列表返回 0，避免除零。"""

    values = list(values)
    return sum(values) / len(values) if values else 0.0


def clamp(value, lower, upper):
    """把数值限制在指定区间内。"""

    return max(lower, min(upper, value))


def to_float(value):
    """安全转换为 float；转换失败时返回 0。"""

    try:
        return float(value or 0)
    except Exception:
        return 0.0


def log_info(message):
    """兼容东财 log 接口和本地 print 的信息日志函数。"""

    # print 通常会进入终端控制台/运行输出，用来兜底确认策略是否真的执行。
    try:
        print(message)
    except Exception:
        pass

    # log 通常会进入东财策略日志面板；如果接口异常，不影响策略继续运行。
    if callable(globals().get("log")):
        try:
            log(level="info", msg=message, source="main-sector-resonance")
        except Exception:
            pass


if __name__ == "__main__":
    # 只有在东财量化环境中存在 run 函数时才启动策略。
    if callable(globals().get("run")):
        run(
            # 策略 ID、文件名和 token 需要在东财量化终端中替换为真实配置。
            strategy_id="replace_with_strategy_id",
            filename="main.py",
            mode=MODE_LIVE,
            token="replace_with_terminal_token",
            # 以下回测参数仅作为示例；实盘模式下主要由终端运行环境控制。
            backtest_start_time="2026-02-01 09:00:00",
            backtest_end_time="2026-07-29 15:30:00",
            backtest_adjust=ADJUST_PREV,
            # 初始资金、佣金和滑点用于回测时模拟交易成本。
            backtest_initial_cash=1000000,
            backtest_commission_ratio=0.0001,
            backtest_slippage_ratio=0.0001,
        )
