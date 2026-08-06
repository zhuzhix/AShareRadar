from __future__ import annotations

import argparse
import csv
import datetime as dt
import re
import sys
import time
from pathlib import Path


LOCAL_PACKAGE_DIR = Path(__file__).resolve().parents[1] / "history_update" / ".python_packages"
if LOCAL_PACKAGE_DIR.exists():
    sys.path.insert(0, str(LOCAL_PACKAGE_DIR))

import akshare as ak  # noqa: E402


DEFAULT_OUTPUT = (
    Path(__file__).resolve().parents[2]
    / "src"
    / "AShareRadar.ServiceHost"
    / "data"
    / "concept-mapping.csv"
)

RETRY_ATTEMPTS = 4
RETRY_DELAY = 2.0
EPHEMERAL_KEYWORDS = [
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
]


def normalize_symbol(value: object) -> str:
    text = str(value or "").strip()
    text = text.replace("sh.", "").replace("sz.", "")
    digits = re.sub(r"\D", "", text)
    return digits[-6:] if len(digits) >= 6 else digits


def build_concept_code(name: str, fallback: object) -> str:
    raw_code = str(fallback or "").strip().lower()
    if raw_code:
        return re.sub(r"[^0-9a-zA-Z_-]+", "-", raw_code).strip("-").lower()

    return re.sub(r"\s+", "-", name.strip()).lower()


def pick_column(columns: list[str], candidates: list[str]) -> str | None:
    normalized = {column.strip().lower(): column for column in columns}
    for candidate in candidates:
        if candidate.strip().lower() in normalized:
            return normalized[candidate.strip().lower()]
    return None


def fetch_concept_boards(limit: int) -> list[dict[str, str]]:
    frame = retry_call("concept board list", ak.stock_board_concept_name_em)
    columns = [str(item) for item in frame.columns]
    name_column = pick_column(columns, ["板块名称", "概念名称", "名称"])
    code_column = pick_column(columns, ["板块代码", "概念代码", "代码"])
    if name_column is None:
        raise RuntimeError(f"cannot find concept name column, columns={columns}")

    rows: list[dict[str, str]] = []
    for _, row in frame.iterrows():
        name = str(row.get(name_column, "")).strip()
        if not name:
            continue

        code = build_concept_code(name, row.get(code_column, "") if code_column else "")
        rows.append({"concept_code": code, "concept_name": name})

    return rows[:limit] if limit > 0 else rows


def fetch_concept_members(concept_name: str) -> list[dict[str, str]]:
    frame = retry_call(
        f"concept members {concept_name}",
        lambda: ak.stock_board_concept_cons_em(symbol=concept_name),
    )
    columns = [str(item) for item in frame.columns]
    symbol_column = pick_column(columns, ["代码", "股票代码"])
    name_column = pick_column(columns, ["名称", "股票名称"])
    if symbol_column is None:
        raise RuntimeError(f"cannot find stock code column for {concept_name}, columns={columns}")

    rows: list[dict[str, str]] = []
    for _, row in frame.iterrows():
        symbol = normalize_symbol(row.get(symbol_column, ""))
        if not symbol:
            continue

        rows.append(
            {
                "symbol": symbol,
                "stock_name": str(row.get(name_column, "")).strip() if name_column else "",
            }
        )

    return rows


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=["symbol", "concept_code", "concept_name", "source", "updated_at"],
        )
        writer.writeheader()
        writer.writerows(
            sorted(
                rows,
                key=lambda item: (item["symbol"], item["concept_code"], item["concept_name"]),
            )
        )


def read_existing_csv(path: Path) -> list[dict[str, str]]:
    if not path.exists():
        return []

    with path.open("r", newline="", encoding="utf-8-sig") as handle:
        reader = csv.DictReader(handle)
        rows: list[dict[str, str]] = []
        for row in reader:
            symbol = normalize_symbol(row.get("symbol", ""))
            concept_code = str(row.get("concept_code", "")).strip()
            concept_name = str(row.get("concept_name", "")).strip()
            if not symbol or not concept_code or not concept_name:
                continue

            rows.append(
                {
                    "symbol": symbol,
                    "concept_code": concept_code,
                    "concept_name": concept_name,
                    "source": str(row.get("source", "")).strip() or "existing",
                    "updated_at": str(row.get("updated_at", "")).strip() or dt.date.today().isoformat(),
                }
            )

        return rows


def retry_call(label: str, action, attempts: int | None = None, delay: float | None = None):
    attempts = attempts or RETRY_ATTEMPTS
    delay = delay or RETRY_DELAY
    last_error: Exception | None = None
    for attempt in range(1, attempts + 1):
        try:
            return action()
        except Exception as exc:  # noqa: BLE001
            last_error = exc
            if attempt == attempts:
                break

            wait_seconds = delay * attempt
            print(
                f"warn: {label} failed on attempt {attempt}/{attempts}: "
                f"{type(exc).__name__}: {exc}; retry in {wait_seconds:.1f}s",
                file=sys.stderr,
            )
            time.sleep(wait_seconds)

    assert last_error is not None
    raise last_error


def main() -> int:
    global RETRY_ATTEMPTS, RETRY_DELAY

    parser = argparse.ArgumentParser(description="Update A-share concept mapping from AkShare EastMoney boards.")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--start", type=int, default=0, help="Zero-based concept board start index.")
    parser.add_argument("--limit", type=int, default=0, help="Concept board count for this run. 0 means to the end.")
    parser.add_argument("--append", action="store_true", help="Append to existing output instead of replacing it.")
    parser.add_argument("--sleep", type=float, default=0.15, help="Delay between board requests.")
    parser.add_argument("--attempts", type=int, default=2, help="Retry attempts per request.")
    parser.add_argument("--retry-delay", type=float, default=1.0, help="Base retry delay seconds.")
    parser.add_argument("--include-ephemeral", action="store_true", help="Include dynamic boards like recent highs/limits.")
    args = parser.parse_args()
    RETRY_ATTEMPTS = max(1, args.attempts)
    RETRY_DELAY = max(0.1, args.retry_delay)

    all_boards = fetch_concept_boards(0)
    if not args.include_ephemeral:
        all_boards = [
            item for item in all_boards
            if not any(keyword.lower() in item["concept_name"].lower() for keyword in EPHEMERAL_KEYWORDS)
        ]
    end = None if args.limit <= 0 else args.start + args.limit
    boards = all_boards[args.start:end]
    rows: list[dict[str, str]] = read_existing_csv(args.output) if args.append else []
    seen: set[tuple[str, str]] = {
        (row["symbol"], row["concept_code"])
        for row in rows
    }
    today = dt.date.today().isoformat()

    for index, board in enumerate(boards, start=1):
        global_index = args.start + index
        concept_code = board["concept_code"]
        concept_name = board["concept_name"]
        try:
            members = fetch_concept_members(concept_name)
        except Exception as exc:
            print(f"warn: concept {concept_name} failed: {type(exc).__name__}: {exc}", file=sys.stderr)
            continue

        for member in members:
            key = (member["symbol"], concept_code)
            if key in seen:
                continue

            seen.add(key)
            rows.append(
                {
                    "symbol": member["symbol"],
                    "concept_code": concept_code,
                    "concept_name": concept_name,
                    "source": "akshare-eastmoney",
                    "updated_at": today,
                }
            )

        write_csv(args.output, rows)

        if index % 10 == 0 or index == len(boards):
            print(
                f"processed {index}/{len(boards)} concepts "
                f"(global {global_index}/{len(all_boards)}), mappings={len(rows)}"
            )

        if args.sleep > 0:
            time.sleep(args.sleep)

    if not rows:
        raise RuntimeError("no concept mappings fetched; output file was not changed")

    write_csv(args.output, rows)
    print(
        f"wrote {len(rows)} concept mappings from {len(boards)} concepts "
        f"(start={args.start}, total_boards={len(all_boards)}) to {args.output}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
