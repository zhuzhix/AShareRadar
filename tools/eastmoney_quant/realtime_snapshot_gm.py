import argparse
import json
import os
import sys
import time
from datetime import datetime
from pathlib import Path

import duckdb
from gm.api import current, get_instruments, set_token


ASHARE_SYMBOL_SQL = """
WITH latest AS (
    SELECT max(date) AS latest_date
    FROM daily_bars
    WHERE adjustflag = '2'
      AND tradestatus = 1
      AND isST = 0
)
SELECT code, any_value(code_name) AS code_name
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
LIMIT ?;
"""


PRE_CLOSE_SQL = """
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


def to_gm_symbol(code: str) -> str:
    value = code.strip().lower()
    if value.startswith("sh."):
        return "SHSE." + value[3:]
    if value.startswith("sz."):
        return "SZSE." + value[3:]
    if value.startswith("sh") and len(value) == 8:
        return "SHSE." + value[2:]
    if value.startswith("sz") and len(value) == 8:
        return "SZSE." + value[2:]
    if len(value) == 6 and value.isdigit():
        return ("SHSE." if value.startswith("6") else "SZSE.") + value
    raise ValueError(f"Unsupported symbol: {code}")


def to_internal_symbol(symbol: str) -> str:
    value = symbol.strip().upper()
    if value.startswith("SHSE."):
        return "sh" + value[5:]
    if value.startswith("SZSE."):
        return "sz" + value[5:]
    return value.lower()


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


def load_universe_from_duckdb(db_path: str, limit: int) -> tuple[list[str], dict[str, str], dict[str, float]]:
    with duckdb.connect(db_path, read_only=True) as connection:
        rows = connection.execute(ASHARE_SYMBOL_SQL, [limit]).fetchall()
        preclose_rows = connection.execute(PRE_CLOSE_SQL).fetchall()

    symbols: list[str] = []
    names: dict[str, str] = {}
    for code, name in rows:
        gm_symbol = to_gm_symbol(str(code))
        symbols.append(gm_symbol)
        names[gm_symbol] = str(name or to_internal_symbol(gm_symbol))

    precloses: dict[str, float] = {}
    for code, preclose, close in preclose_rows:
        baseline = preclose or close
        if baseline:
            precloses[to_gm_symbol(str(code))] = float(baseline)

    return symbols, names, precloses


def load_instrument_names() -> dict[str, str]:
    rows = get_instruments(
        exchanges=["SHSE", "SZSE"],
        sec_types=[1],
        skip_suspended=False,
        skip_st=True,
        fields="symbol,sec_name",
        df=False,
    ) or []

    names: dict[str, str] = {}
    for raw in rows:
        item = normalize_item(raw)
        symbol = str(item.get("symbol", ""))
        name = str(item.get("sec_name") or item.get("name") or "").strip()
        if symbol and name:
            names[symbol] = name
    return names


def fetch_current(symbols: list[str], chunk_size: int) -> list[dict]:
    rows: list[dict] = []
    for index in range(0, len(symbols), chunk_size):
        chunk = symbols[index:index + chunk_size]
        try:
            quotes = current(symbols=",".join(chunk)) or []
            rows.extend(normalize_item(item) for item in quotes)
            print(
                f"[eastmoney-quant-realtime] batch={index // chunk_size + 1} size={len(chunk)} returned={len(quotes)}",
                file=sys.stderr,
                flush=True,
            )
        except Exception as exc:
            print(
                f"[eastmoney-quant-realtime:batch-error] batch={index // chunk_size + 1} size={len(chunk)} {type(exc).__name__}: {exc}",
                file=sys.stderr,
                flush=True,
            )
    return rows


def to_quote(item: dict, names: dict[str, str], precloses: dict[str, float]) -> dict | None:
    symbol = str(item.get("symbol", ""))
    price = read_number(item, "price")
    if not symbol or price <= 0:
        return None

    preclose = precloses.get(symbol, 0.0)
    change_percent = (price / preclose - 1.0) * 100.0 if preclose > 0 else 0.0
    quote_time = str(item.get("created_at") or datetime.now().isoformat())

    return {
        "symbol": to_internal_symbol(symbol),
        "name": names.get(symbol) or to_internal_symbol(symbol),
        "price": round(price, 4),
        "changePercent": round(change_percent, 4),
        "volumeRatio": 0,
        "turnoverRate": 0,
        "amount": round(read_number(item, "cum_amount", "amount"), 4),
        "quoteTime": quote_time,
        "open": round(read_number(item, "open"), 4),
        "high": round(read_number(item, "high"), 4),
        "low": round(read_number(item, "low"), 4),
        "volume": round(read_number(item, "cum_volume", "volume"), 4),
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Load an A-share realtime snapshot from EastMoney Quant SDK.")
    parser.add_argument("--db", required=True)
    parser.add_argument("--max-symbols", type=int, default=6000)
    parser.add_argument("--batch-size", type=int, default=200)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    token = os.environ.get("EASTMONEY_QUANT_TOKEN", "").strip()
    if not token:
        raise RuntimeError("EASTMONEY_QUANT_TOKEN is required.")

    started = time.perf_counter()
    set_token(token)
    symbols, names, precloses = load_universe_from_duckdb(args.db, args.max_symbols)
    names.update(load_instrument_names())
    quotes = [
        quote
        for quote in (to_quote(item, names, precloses) for item in fetch_current(symbols, max(1, args.batch_size)))
        if quote is not None
    ]

    print(json.dumps({
        "snapshotTime": datetime.now().isoformat(),
        "providerName": "EastMoneyQuant",
        "requested": len(symbols),
        "returned": len(quotes),
        "elapsedSeconds": round(time.perf_counter() - started, 3),
        "quotes": quotes,
    }, ensure_ascii=False, default=str))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"[eastmoney-quant-realtime:error] {type(exc).__name__}: {exc}", file=sys.stderr, flush=True)
        raise
