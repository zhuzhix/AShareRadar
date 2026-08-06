from __future__ import annotations

import argparse
import os
import sys
import time
from dataclasses import dataclass
from datetime import date, datetime, timedelta
from pathlib import Path

LOCAL_PACKAGES = Path(__file__).resolve().parent / ".python_packages"
if LOCAL_PACKAGES.exists():
    sys.path.insert(0, str(LOCAL_PACKAGES))

import baostock as bs
import duckdb
import pandas as pd


FIELDS = ",".join(
    [
        "date",
        "code",
        "open",
        "high",
        "low",
        "close",
        "preclose",
        "volume",
        "amount",
        "adjustflag",
        "turn",
        "tradestatus",
        "pctChg",
        "peTTM",
        "pbMRQ",
        "psTTM",
        "pcfNcfTTM",
        "isST",
    ]
)


@dataclass(frozen=True)
class Paths:
    root: Path

    @property
    def parquet(self) -> Path:
        return self.root / "parquet"

    @property
    def duckdb_path(self) -> Path:
        return self.root / "ashare.duckdb"

    @property
    def temp_duckdb_path(self) -> Path:
        return self.root / "ashare.duckdb.updating"

    @property
    def daily_dir(self) -> Path:
        return self.parquet / "daily_bars"

    @property
    def universe_path(self) -> Path:
        return self.parquet / "stock_universe.parquet"

    @property
    def calendar_path(self) -> Path:
        return self.parquet / "trade_calendar.parquet"


def fetch_frame(query) -> pd.DataFrame:
    rows: list[list[str]] = []
    while query.error_code == "0" and query.next():
        rows.append(query.get_row_data())
    if query.error_code != "0":
        raise RuntimeError(f"BaoStock query failed: {query.error_code} {query.error_msg}")
    return pd.DataFrame(rows, columns=query.fields)


def stock_output_path(paths: Paths, code: str) -> Path:
    return paths.daily_dir / f"{code.replace('.', '_')}.parquet"


def load_last_bar_date(paths: Paths) -> date:
    if paths.duckdb_path.exists():
        conn = duckdb.connect(str(paths.duckdb_path), read_only=True)
        try:
            value = conn.execute("SELECT max(date) FROM daily_bars").fetchone()[0]
            if value is not None:
                return pd.to_datetime(value).date()
        finally:
            conn.close()

    dates: list[date] = []
    for item in paths.daily_dir.glob("*.parquet"):
        frame = pd.read_parquet(item, columns=["date"])
        if not frame.empty:
            dates.append(pd.to_datetime(frame["date"]).dt.date.max())
    if not dates:
        raise RuntimeError("No existing historical bars found.")
    return max(dates)


def load_trade_calendar(start: date, end: date, paths: Paths, write_calendar: bool) -> pd.DataFrame:
    frame = fetch_frame(bs.query_trade_dates(start_date=start.isoformat(), end_date=end.isoformat()))
    if frame.empty:
        return frame

    frame["calendar_date"] = pd.to_datetime(frame["calendar_date"]).dt.date
    frame["is_trading_day"] = pd.to_numeric(frame["is_trading_day"], errors="coerce").fillna(0).astype(int)

    if write_calendar and paths.calendar_path.exists():
        existing = pd.read_parquet(paths.calendar_path)
        existing["calendar_date"] = pd.to_datetime(existing["calendar_date"]).dt.date
        frame = pd.concat([existing, frame], ignore_index=True)
        frame = frame.drop_duplicates(subset=["calendar_date"], keep="last")

    frame = frame.sort_values("calendar_date").reset_index(drop=True)
    if write_calendar:
        frame.to_parquet(paths.calendar_path, index=False)
    return frame


def normalize_daily(frame: pd.DataFrame, board: str, code_name: str) -> pd.DataFrame:
    frame = frame.copy()
    frame["board"] = board
    frame["code_name"] = code_name
    numeric_cols = [
        "open",
        "high",
        "low",
        "close",
        "preclose",
        "volume",
        "amount",
        "turn",
        "pctChg",
        "peTTM",
        "pbMRQ",
        "psTTM",
        "pcfNcfTTM",
    ]
    for col in numeric_cols:
        frame[col] = pd.to_numeric(frame[col], errors="coerce")
    frame["isST"] = pd.to_numeric(frame["isST"], errors="coerce").fillna(0).astype(int)
    frame["tradestatus"] = pd.to_numeric(frame["tradestatus"], errors="coerce").fillna(0).astype(int)
    frame["date"] = pd.to_datetime(frame["date"]).dt.date
    return frame


def merge_daily_file(paths: Paths, incoming: pd.DataFrame, code: str) -> int:
    if incoming.empty:
        return 0

    path = stock_output_path(paths, code)
    if path.exists():
        existing = pd.read_parquet(path)
        existing["date"] = pd.to_datetime(existing["date"]).dt.date
        merged = pd.concat([existing, incoming], ignore_index=True)
    else:
        merged = incoming

    before = 0 if not path.exists() else len(pd.read_parquet(path, columns=["date"]))
    merged = merged.drop_duplicates(subset=["code", "date"], keep="last")
    merged = merged.sort_values(["code", "date"]).reset_index(drop=True)
    merged.to_parquet(path, index=False)
    return max(0, len(merged) - before)


def rebuild_duckdb(paths: Paths) -> None:
    if paths.temp_duckdb_path.exists():
        paths.temp_duckdb_path.unlink()

    conn = duckdb.connect(str(paths.temp_duckdb_path))
    try:
        conn.execute("CREATE OR REPLACE TABLE stock_universe AS SELECT * FROM read_parquet(?)", [str(paths.universe_path)])
        conn.execute("CREATE OR REPLACE TABLE trade_calendar AS SELECT * FROM read_parquet(?)", [str(paths.calendar_path)])
        pattern = str(paths.daily_dir / "*.parquet")
        conn.execute("CREATE OR REPLACE TABLE daily_bars AS SELECT * FROM read_parquet(?)", [pattern])
        conn.execute("CREATE INDEX IF NOT EXISTS idx_daily_code_date ON daily_bars(code, date)")
        conn.execute("CREATE INDEX IF NOT EXISTS idx_daily_date ON daily_bars(date)")
    finally:
        conn.close()

    for attempt in range(1, 7):
        try:
            os.replace(paths.temp_duckdb_path, paths.duckdb_path)
            return
        except PermissionError:
            if attempt == 6:
                raise
            time.sleep(2)


def update_missing_bars(paths: Paths, missing_dates: set[date], adjustflag: str, limit: int) -> tuple[int, int]:
    universe = pd.read_parquet(paths.universe_path)
    items = universe.to_dict("records")
    if limit > 0:
        items = items[:limit]

    start = min(missing_dates).isoformat()
    end = max(missing_dates).isoformat()
    touched_stocks = 0
    inserted_rows = 0

    for idx, row in enumerate(items, start=1):
        code = str(row["code"])
        print(f"[daily-update] {idx}/{len(items)} {code} {row.get('code_name', '')}", flush=True)
        query = bs.query_history_k_data_plus(
            code,
            FIELDS,
            start_date=start,
            end_date=end,
            frequency="d",
            adjustflag=adjustflag,
        )
        frame = fetch_frame(query)
        if not frame.empty:
            frame = normalize_daily(frame, str(row.get("board", "")), str(row.get("code_name", "")))
            frame = frame[frame["date"].isin(missing_dates)]
            inserted = merge_daily_file(paths, frame, code)
            inserted_rows += inserted
            if inserted > 0:
                touched_stocks += 1
        time.sleep(0.08)

    return touched_stocks, inserted_rows


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Incrementally update A-share historical daily bars.")
    parser.add_argument("--data-dir", required=True)
    parser.add_argument("--end", default=date.today().isoformat())
    parser.add_argument("--adjustflag", default="2", choices=["1", "2", "3"])
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--dry-run", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    paths = Paths(Path(args.data_dir).resolve())
    end = pd.to_datetime(args.end).date()
    last_date = load_last_bar_date(paths)
    start = last_date + timedelta(days=1)

    if start > end:
        print(f"[history-update] no calendar gap. last_date={last_date} end={end}", flush=True)
        return 0

    login = bs.login()
    if login.error_code != "0":
        raise RuntimeError(f"BaoStock login failed: {login.error_code} {login.error_msg}")

    try:
        calendar = load_trade_calendar(start, end, paths, write_calendar=not args.dry_run)
        missing_dates = {
            item
            for item in calendar.loc[calendar["is_trading_day"] == 1, "calendar_date"].tolist()
            if item > last_date
        }

        if not missing_dates:
            print(f"[history-update] no missing trading day. last_date={last_date} end={end}", flush=True)
            return 0

        print(
            "[history-update] missing_dates="
            + ",".join(item.isoformat() for item in sorted(missing_dates)),
            flush=True,
        )

        if args.dry_run:
            return 0

        touched_stocks, inserted_rows = update_missing_bars(paths, missing_dates, args.adjustflag, args.limit)
        rebuild_duckdb(paths)
        print(
            f"[history-update] completed. touched_stocks={touched_stocks} inserted_rows={inserted_rows} "
            f"from={min(missing_dates)} to={max(missing_dates)} duckdb={paths.duckdb_path}",
            flush=True,
        )
        return 0
    finally:
        bs.logout()


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"[history-update:error] {exc}", file=sys.stderr, flush=True)
        raise
