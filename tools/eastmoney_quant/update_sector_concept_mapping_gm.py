from __future__ import annotations

import argparse
import csv
import os
import sys
import time
from datetime import datetime
from pathlib import Path
from typing import Any, Iterable

from gm.api import (  # type: ignore
    get_symbol_infos,
    set_token,
    stk_get_symbol_industry,
    stk_get_symbol_sector,
)


DYNAMIC_CONCEPT_KEYWORDS = (
    "昨日",
    "连板",
    "涨停",
    "新高",
    "超跌",
    "低价股",
    "微盘股",
    "百日",
    "近期",
    "破发",
    "破净",
    "预亏",
    "预增",
    "ST",
)


def main() -> int:
    parser = argparse.ArgumentParser(description="Update A-share industry/concept mapping from EastMoney SDK.")
    parser.add_argument("--output-dir", default="src/AShareRadar.ServiceHost/data")
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--sleep-seconds", type=float, default=0.03)
    parser.add_argument("--include-dynamic-concepts", action="store_true")
    args = parser.parse_args()

    token = os.environ.get("EASTMONEY_QUANT_TOKEN", "").strip()
    if not token:
        print("[mapping-update] EASTMONEY_QUANT_TOKEN is not set.", file=sys.stderr)
        return 2

    set_token(token)
    symbols = load_symbols(args.limit)
    if not symbols:
        print("[mapping-update] no symbols returned from SDK.", file=sys.stderr)
        return 3

    output_dir = Path(args.output_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    sector_rows: list[dict[str, str]] = []
    concept_rows: list[dict[str, str]] = []
    updated_at = datetime.now().strftime("%Y-%m-%d %H:%M:%S")

    print(f"[mapping-update] symbols={len(symbols)}")
    for index, symbol in enumerate(symbols, start=1):
        try:
            industry_rows = call_sdk(stk_get_symbol_industry, symbol)
            sector_rows.extend(parse_industry_rows(symbol, industry_rows, updated_at))

            concept_candidates = call_sdk(stk_get_symbol_sector, symbol)
            concept_rows.extend(
                parse_concept_rows(
                    symbol,
                    concept_candidates,
                    updated_at,
                    include_dynamic=args.include_dynamic_concepts,
                )
            )
        except Exception as exc:
            print(f"[mapping-update] {symbol} failed: {exc}", file=sys.stderr)

        if index % 100 == 0 or index == len(symbols):
            print(
                f"[mapping-update] progress={index}/{len(symbols)} "
                f"sector_rows={len(sector_rows)} concept_rows={len(concept_rows)}"
            )

        if args.sleep_seconds > 0:
            time.sleep(args.sleep_seconds)

    sector_path = output_dir / "sector-mapping.csv"
    concept_path = output_dir / "concept-mapping.csv"
    write_csv(
        sector_path,
        ["symbol", "sector_code", "sector_name", "source", "updated_at"],
        dedupe_rows(sector_rows, ("symbol", "sector_code")),
    )
    write_csv(
        concept_path,
        ["symbol", "concept_code", "concept_name", "source", "updated_at"],
        dedupe_rows(concept_rows, ("symbol", "concept_code")),
    )
    print(f"[mapping-update] wrote sector={sector_path} rows={len(sector_rows)}")
    print(f"[mapping-update] wrote concept={concept_path} rows={len(concept_rows)}")
    return 0


def load_symbols(limit: int) -> list[str]:
    rows = get_symbol_infos(sec_type1=1010, sec_type2=101001, exchanges=["SHSE", "SZSE"])
    symbols: list[str] = []
    for row in normalize_rows(rows):
        symbol = str(get_value(row, "symbol") or "").strip()
        sec_name = str(get_value(row, "sec_name", "name") or "")
        listed_sector = str(get_value(row, "listed_sector") or "")
        delisted_date = str(get_value(row, "delisted_date") or "")
        if not is_ashare_symbol(symbol):
            continue
        if "ST" in sec_name.upper() or "退" in sec_name or listed_sector == "4" or delisted_date:
            continue
        symbols.append(symbol)

    symbols = sorted(set(symbols))
    return symbols[:limit] if limit > 0 else symbols


def call_sdk(func: Any, symbol: str) -> list[Any]:
    try:
        rows = func(symbol=symbol)
    except TypeError:
        rows = func(symbol)
    return list(normalize_rows(rows))


def parse_industry_rows(symbol: str, rows: Iterable[Any], updated_at: str) -> list[dict[str, str]]:
    parsed: list[dict[str, str]] = []
    for row in rows:
        code = str(get_value(row, "industry_code", "code") or "").strip()
        name = str(get_value(row, "industry_name", "name") or "").strip()
        if code and name:
            parsed.append(
                {
                    "symbol": normalize_symbol(symbol),
                    "sector_code": code,
                    "sector_name": name,
                    "source": "EastMoneySdk",
                    "updated_at": updated_at,
                }
            )
    return parsed


def parse_concept_rows(
    symbol: str,
    rows: Iterable[Any],
    updated_at: str,
    include_dynamic: bool,
) -> list[dict[str, str]]:
    parsed: list[dict[str, str]] = []
    for row in rows:
        sector_type = str(get_value(row, "sector_type", "type") or "").strip()
        if sector_type and sector_type != "1003":
            continue

        code = str(get_value(row, "sector_code", "code") or "").strip()
        name = str(get_value(row, "sector_name", "name") or "").strip()
        if not code or not name:
            continue
        if not include_dynamic and is_dynamic_concept(name):
            continue

        parsed.append(
            {
                "symbol": normalize_symbol(symbol),
                "concept_code": code,
                "concept_name": name,
                "source": "EastMoneySdk",
                "updated_at": updated_at,
            }
        )
    return parsed


def normalize_rows(rows: Any) -> list[Any]:
    if rows is None:
        return []
    if hasattr(rows, "to_dict"):
        return rows.to_dict("records")
    if isinstance(rows, dict):
        return [rows]
    return list(rows)


def get_value(row: Any, *names: str) -> Any:
    for name in names:
        if isinstance(row, dict) and name in row:
            return row[name]
        if hasattr(row, name):
            return getattr(row, name)
    return None


def is_ashare_symbol(symbol: str) -> bool:
    return symbol.startswith(("SHSE.60", "SHSE.68", "SZSE.00", "SZSE.30"))


def normalize_symbol(symbol: str) -> str:
    return symbol.split(".")[-1]


def is_dynamic_concept(name: str) -> bool:
    return any(keyword in name for keyword in DYNAMIC_CONCEPT_KEYWORDS)


def dedupe_rows(rows: list[dict[str, str]], keys: tuple[str, ...]) -> list[dict[str, str]]:
    result: list[dict[str, str]] = []
    seen: set[tuple[str, ...]] = set()
    for row in rows:
        key = tuple(row[item] for item in keys)
        if key in seen:
            continue
        seen.add(key)
        result.append(row)
    return result


def write_csv(path: Path, fieldnames: list[str], rows: list[dict[str, str]]) -> None:
    with path.open("w", encoding="utf-8-sig", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=fieldnames)
        writer.writeheader()
        writer.writerows(rows)


if __name__ == "__main__":
    raise SystemExit(main())
