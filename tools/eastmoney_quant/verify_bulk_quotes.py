import argparse
import json
import os
import time
from datetime import datetime
from pathlib import Path

import duckdb
from gm.api import current, set_token


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
LIMIT ?;
"""


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


def load_symbols(db_path: str, limit: int) -> list[str]:
    with duckdb.connect(db_path, read_only=True) as connection:
        rows = connection.execute(SYMBOL_POOL_SQL, [limit]).fetchall()
    return [to_gm_symbol(row[0]) for row in rows]


def fetch_batch(symbols: list[str]) -> tuple[int, float, str | None]:
    started = time.perf_counter()
    try:
        quotes = current(symbols=",".join(symbols))
        elapsed = time.perf_counter() - started
        return len(quotes or []), elapsed, None
    except Exception as exc:
        elapsed = time.perf_counter() - started
        return 0, elapsed, f"{type(exc).__name__}: {exc}"


def run_chunked(symbols: list[str], chunk_size: int) -> dict:
    total = 0
    failures = []
    started = time.perf_counter()
    batch_elapsed = []

    for index in range(0, len(symbols), chunk_size):
        chunk = symbols[index:index + chunk_size]
        count, elapsed, error = fetch_batch(chunk)
        total += count
        batch_elapsed.append(round(elapsed, 3))
        if error:
            failures.append({
                "batch": index // chunk_size + 1,
                "size": len(chunk),
                "error": error,
            })

    return {
        "chunk_size": chunk_size,
        "batch_count": (len(symbols) + chunk_size - 1) // chunk_size,
        "requested": len(symbols),
        "returned": total,
        "elapsed_seconds": round(time.perf_counter() - started, 3),
        "batch_elapsed_seconds": batch_elapsed,
        "failures": failures,
    }


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--db", required=True)
    parser.add_argument("--limit", type=int, default=6000)
    parser.add_argument("--chunks", default="80,200,500,1000")
    parser.add_argument("--single", action="store_true")
    args = parser.parse_args()

    token = os.environ.get("EASTMONEY_QUANT_TOKEN", "").strip()
    if not token:
        raise RuntimeError("EASTMONEY_QUANT_TOKEN is required.")

    set_token(token)
    symbols = load_symbols(args.db, args.limit)
    payload = {
        "checked_at": datetime.now().isoformat(timespec="seconds"),
        "db": args.db,
        "symbol_count": len(symbols),
        "first_symbols": symbols[:5],
        "last_symbols": symbols[-5:],
        "single_request": None,
        "chunked_requests": [],
    }

    if args.single:
        count, elapsed, error = fetch_batch(symbols)
        payload["single_request"] = {
            "requested": len(symbols),
            "returned": count,
            "elapsed_seconds": round(elapsed, 3),
            "error": error,
        }

    for chunk_size in [int(item) for item in args.chunks.split(",") if item.strip()]:
        payload["chunked_requests"].append(run_chunked(symbols, chunk_size))

    output_path = Path(__file__).with_name("verify_bulk_quotes_result.json")
    output_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps({
        "checked_at": payload["checked_at"],
        "symbol_count": payload["symbol_count"],
        "single_request": payload["single_request"],
        "chunked_summary": [
            {
                "chunk_size": item["chunk_size"],
                "returned": item["returned"],
                "elapsed_seconds": item["elapsed_seconds"],
                "failure_count": len(item["failures"]),
            }
            for item in payload["chunked_requests"]
        ],
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
