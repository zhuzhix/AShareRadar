import json
import os
import time
from datetime import datetime, timedelta
from pathlib import Path

import duckdb
from gm.api import current, history, set_token


DB_PATH = r"C:\Users\Administrator\Documents\Codex\2026-07-22\zhe\AShareSignalMonitor\data\ashare.duckdb"

SYMBOL_POOL_SQL = """
WITH latest AS (
    SELECT max(date) AS latest_date
    FROM daily_bars
    WHERE adjustflag = '2'
      AND tradestatus = 1
      AND isST = 0
)
SELECT code
FROM daily_bars, latest
WHERE adjustflag = '2'
  AND tradestatus = 1
  AND isST = 0
  AND close >= 1
  AND date >= latest_date - INTERVAL 260 DAY
  AND (
       code LIKE 'sh.600%'
    OR code LIKE 'sh.601%'
    OR code LIKE 'sh.603%'
    OR code LIKE 'sh.605%'
    OR code LIKE 'sh.688%'
    OR code LIKE 'sh.689%'
    OR code LIKE 'sz.000%'
    OR code LIKE 'sz.001%'
    OR code LIKE 'sz.002%'
    OR code LIKE 'sz.003%'
    OR code LIKE 'sz.300%'
    OR code LIKE 'sz.301%'
  )
  AND upper(code_name) NOT LIKE '%ST%'
GROUP BY code, latest_date
HAVING count(*) >= 120
   AND max(date) >= latest_date - INTERVAL 10 DAY
ORDER BY code ASC
LIMIT 6000;
"""


INDEX_SYMBOLS = [
    "SHSE.000001",
    "SZSE.399001",
    "SZSE.399006",
    "SHSE.000688",
    "SHSE.000300",
    "SHSE.000905",
    "SHSE.000852",
]


def to_gm_symbol(code: str) -> str:
    value = code.strip().lower()
    if value.startswith("sh."):
        return "SHSE." + value[3:]
    if value.startswith("sz."):
        return "SZSE." + value[3:]
    raise ValueError(f"Unsupported code: {code}")


def normalize_item(item):
    if hasattr(item, "to_dict"):
        return item.to_dict()
    if isinstance(item, dict):
        return item
    return {
        key: getattr(item, key)
        for key in dir(item)
        if not key.startswith("_") and not callable(getattr(item, key))
    }


def load_symbols() -> list[str]:
    with duckdb.connect(DB_PATH, read_only=True) as connection:
        rows = connection.execute(SYMBOL_POOL_SQL).fetchall()
    return [to_gm_symbol(row[0]) for row in rows]


def load_latest_preclose() -> dict[str, float]:
    sql = """
    WITH latest AS (
        SELECT max(date) AS latest_date
        FROM daily_bars
        WHERE adjustflag = '2'
          AND tradestatus = 1
          AND isST = 0
    )
    SELECT code, preclose, close
    FROM daily_bars, latest
    WHERE date = latest_date
      AND adjustflag = '2'
      AND tradestatus = 1
      AND isST = 0;
    """
    values: dict[str, float] = {}
    with duckdb.connect(DB_PATH, read_only=True) as connection:
        for code, preclose, close in connection.execute(sql).fetchall():
            symbol = to_gm_symbol(code)
            baseline = preclose or close
            if baseline:
                values[symbol] = float(baseline)
    return values


def read_number(item: dict, *keys: str) -> float:
    for key in keys:
        value = item.get(key)
        if value in (None, "", "-"):
            continue
        try:
            return float(value)
        except (TypeError, ValueError):
            continue
    return 0.0


def fetch_current(symbols: list[str]) -> tuple[list[dict], float, str | None]:
    started = time.perf_counter()
    try:
        rows = current(symbols=",".join(symbols)) or []
        return [normalize_item(item) for item in rows], time.perf_counter() - started, None
    except Exception as exc:
        return [], time.perf_counter() - started, f"{type(exc).__name__}: {exc}"


def summarize_quotes(quotes: list[dict]) -> dict:
    pct_keys = ("change_percent", "change_pct", "pct_change", "pctChg")
    amount_keys = ("cum_amount", "amount", "turnover")
    rising = 0
    falling = 0
    big_rise = 0
    big_fall = 0
    limit_up = 0
    limit_down = 0
    total_amount = 0.0
    with_pct = 0
    with_amount = 0

    for item in quotes:
        pct = read_number(item, *pct_keys)
        amount = read_number(item, *amount_keys)
        if pct != 0:
            with_pct += 1
        if amount > 0:
            with_amount += 1
            total_amount += amount

        if pct > 0:
            rising += 1
        elif pct < 0:
            falling += 1

        if pct >= 5:
            big_rise += 1
        if pct <= -5:
            big_fall += 1
        if pct >= 9.8:
            limit_up += 1
        if pct <= -9.8:
            limit_down += 1

    return {
        "quote_count": len(quotes),
        "with_pct_count": with_pct,
        "with_amount_count": with_amount,
        "rising_count": rising,
        "falling_count": falling,
        "big_rise_count": big_rise,
        "big_fall_count": big_fall,
        "rough_limit_up_count": limit_up,
        "rough_limit_down_count": limit_down,
        "total_amount_yuan": round(total_amount, 2),
        "sample_fields": sorted(list(quotes[0].keys())) if quotes else [],
        "sample_quote": quotes[0] if quotes else None,
    }


def summarize_quotes_with_preclose(quotes: list[dict], preclose_by_symbol: dict[str, float]) -> dict:
    rising = 0
    falling = 0
    flat = 0
    big_rise = 0
    big_fall = 0
    rough_limit_up = 0
    rough_limit_down = 0
    matched = 0
    total_amount = 0.0
    pct_values = []

    for item in quotes:
        symbol = str(item.get("symbol", ""))
        price = read_number(item, "price")
        preclose = preclose_by_symbol.get(symbol, 0.0)
        amount = read_number(item, "cum_amount", "amount")
        if amount > 0:
            total_amount += amount
        if price <= 0 or preclose <= 0:
            continue

        matched += 1
        pct = (price / preclose - 1.0) * 100.0
        pct_values.append(pct)
        if pct > 0.001:
            rising += 1
        elif pct < -0.001:
            falling += 1
        else:
            flat += 1
        if pct >= 5:
            big_rise += 1
        if pct <= -5:
            big_fall += 1
        if pct >= 9.8:
            rough_limit_up += 1
        if pct <= -9.8:
            rough_limit_down += 1

    return {
        "matched_preclose_count": matched,
        "rising_count": rising,
        "falling_count": falling,
        "flat_count": flat,
        "up_ratio": round(rising / matched, 4) if matched else 0,
        "down_ratio": round(falling / matched, 4) if matched else 0,
        "big_rise_count": big_rise,
        "big_fall_count": big_fall,
        "rough_limit_up_count": rough_limit_up,
        "rough_limit_down_count": rough_limit_down,
        "average_change_percent": round(sum(pct_values) / len(pct_values), 4) if pct_values else 0,
        "total_amount_yuan": round(total_amount, 2),
    }


def fetch_history_probe() -> dict:
    end = datetime.now()
    start = end - timedelta(days=45)
    probes = [
        ("SHSE.600000", "1d"),
        ("SZSE.300059", "1d"),
        ("SHSE.000300", "1d"),
    ]
    result = []
    for symbol, frequency in probes:
        started = time.perf_counter()
        try:
            rows = history(
                symbol=symbol,
                frequency=frequency,
                start_time=start.strftime("%Y-%m-%d %H:%M:%S"),
                end_time=end.strftime("%Y-%m-%d %H:%M:%S"),
                fields="symbol,eob,open,high,low,close,volume,amount,pre_close",
                df=False,
            ) or []
            normalized = [normalize_item(item) for item in rows]
            result.append({
                "symbol": symbol,
                "frequency": frequency,
                "returned": len(normalized),
                "elapsed_seconds": round(time.perf_counter() - started, 3),
                "error": None,
                "sample_fields": sorted(list(normalized[0].keys())) if normalized else [],
                "last_bar": normalized[-1] if normalized else None,
            })
        except Exception as exc:
            result.append({
                "symbol": symbol,
                "frequency": frequency,
                "returned": 0,
                "elapsed_seconds": round(time.perf_counter() - started, 3),
                "error": f"{type(exc).__name__}: {exc}",
            })
    return {"probes": result}


def main():
    token = os.environ.get("EASTMONEY_QUANT_TOKEN", "").strip()
    if not token:
        raise RuntimeError("EASTMONEY_QUANT_TOKEN is required.")

    set_token(token)

    symbols = load_symbols()
    preclose_by_symbol = load_latest_preclose()
    quotes, elapsed, error = fetch_current(symbols)
    index_quotes, index_elapsed, index_error = fetch_current(INDEX_SYMBOLS)

    payload = {
        "checked_at": datetime.now().isoformat(timespec="seconds"),
        "terminal_required": True,
        "universe": {
            "requested": len(symbols),
            "first_symbols": symbols[:5],
            "last_symbols": symbols[-5:],
        },
        "realtime_market": {
            "elapsed_seconds": round(elapsed, 3),
            "error": error,
            "summary": summarize_quotes(quotes),
            "derived_with_local_preclose": summarize_quotes_with_preclose(quotes, preclose_by_symbol),
        },
        "realtime_indices": {
            "symbols": INDEX_SYMBOLS,
            "elapsed_seconds": round(index_elapsed, 3),
            "error": index_error,
            "quotes": index_quotes,
        },
        "history_daily": fetch_history_probe(),
    }

    output_path = Path(__file__).with_name("verify_sentiment_data_result.json")
    output_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2, default=str), encoding="utf-8")

    print(json.dumps({
        "checked_at": payload["checked_at"],
        "universe_requested": payload["universe"]["requested"],
        "market_error": payload["realtime_market"]["error"],
        "market_summary": payload["realtime_market"]["summary"],
        "index_error": payload["realtime_indices"]["error"],
        "index_count": len(payload["realtime_indices"]["quotes"]),
        "history": [
            {
                "symbol": item["symbol"],
                "returned": item["returned"],
                "error": item["error"],
            }
            for item in payload["history_daily"]["probes"]
        ],
    }, ensure_ascii=False, default=str))


if __name__ == "__main__":
    main()
