from __future__ import annotations

import argparse
import json
import os
import sys
import time
from datetime import datetime, timedelta

from gm.api import history, set_token
from gm.enum import ADJUST_PREV


FIELDS = "symbol,eob,open,high,low,close,volume,amount"


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


def read_number(item: dict, key: str) -> float:
    value = item.get(key)
    if value in (None, "", "-"):
        return 0.0
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


def normalize_period(period: str) -> tuple[str, int]:
    value = period.strip().lower()
    if value in ("minute", "m1"):
        return "60s", 1
    if value == "five-day":
        return "60s", 8
    if value == "m5":
        return "300s", 3
    if value == "m15":
        return "900s", 8
    if value == "m30":
        return "1800s", 16
    if value == "m60":
        return "3600s", 32
    raise ValueError(f"Unsupported intraday period: {period}")


def to_bar(item: dict) -> dict | None:
    eob = item.get("eob")
    if not eob:
        return None
    return {
        "tradingTime": str(eob),
        "open": round(read_number(item, "open"), 4),
        "high": round(read_number(item, "high"), 4),
        "low": round(read_number(item, "low"), 4),
        "close": round(read_number(item, "close"), 4),
        "volume": round(read_number(item, "volume"), 4),
        "amount": round(read_number(item, "amount"), 4),
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Load intraday K-line bars from EastMoney Quant SDK.")
    parser.add_argument("--symbol", required=True)
    parser.add_argument("--period", required=True)
    parser.add_argument("--count", type=int, default=240)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    token = os.environ.get("EASTMONEY_QUANT_TOKEN", "").strip()
    if not token:
        raise RuntimeError("EASTMONEY_QUANT_TOKEN is required.")

    started = time.perf_counter()
    frequency, lookback_days = normalize_period(args.period)
    take_count = max(1, min(args.count, 1200))
    now = datetime.now()
    start_time = now - timedelta(days=lookback_days)

    set_token(token)
    rows = history(
        symbol=to_gm_symbol(args.symbol),
        frequency=frequency,
        start_time=start_time.strftime("%Y-%m-%d %H:%M:%S"),
        end_time=now.strftime("%Y-%m-%d %H:%M:%S"),
        fields=FIELDS,
        adjust=ADJUST_PREV,
        df=False,
    ) or []
    bars = [
        bar
        for bar in (to_bar(normalize_item(item)) for item in rows)
        if bar is not None and bar["close"] > 0
    ][-take_count:]

    print(json.dumps({
        "snapshotTime": datetime.now().isoformat(),
        "providerName": "EastMoneyQuantKLine",
        "symbol": args.symbol,
        "period": args.period,
        "frequency": frequency,
        "requested": take_count,
        "returned": len(bars),
        "elapsedSeconds": round(time.perf_counter() - started, 3),
        "bars": bars,
    }, ensure_ascii=False, default=str))
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"[eastmoney-quant-kline:error] {type(exc).__name__}: {exc}", file=sys.stderr, flush=True)
        raise
