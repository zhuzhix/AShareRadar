from __future__ import annotations

import argparse
import os
import shutil
import sys
import time
from dataclasses import dataclass
from datetime import date, datetime, timedelta
from pathlib import Path

LOCAL_PACKAGES = Path(__file__).resolve().parent / ".python_packages"
if LOCAL_PACKAGES.exists():
    sys.path.append(str(LOCAL_PACKAGES))
SHARED_PACKAGES = Path(__file__).resolve().parents[1] / "history_update" / ".python_packages"
if SHARED_PACKAGES.exists():
    sys.path.append(str(SHARED_PACKAGES))

import duckdb
import pandas as pd
from gm.api import get_instruments, history, set_token
try:
    from gm.api import stk_get_daily_basic
except ImportError:  # pragma: no cover - depends on installed SDK version.
    stk_get_daily_basic = None
from gm.enum import ADJUST_PREV


FIELDS = "symbol,eob,open,high,low,close,volume,amount,pre_close"
EXCHANGES = ["SHSE", "SZSE"]
SEC_TYPES = [1]


@dataclass(frozen=True)
class Paths:
    root: Path

    @property
    def parquet(self) -> Path:
        return self.root / "parquet"

    @property
    def daily_dir(self) -> Path:
        return self.parquet / "daily_bars"

    @property
    def daily_basic_dir(self) -> Path:
        return self.parquet / "daily_basic"

    @property
    def universe_path(self) -> Path:
        return self.parquet / "stock_universe.parquet"

    @property
    def calendar_path(self) -> Path:
        return self.parquet / "trade_calendar.parquet"

    @property
    def duckdb_path(self) -> Path:
        return self.root / "ashare.duckdb"

    @property
    def temp_duckdb_path(self) -> Path:
        return self.root / "ashare.duckdb.updating"

    @property
    def backup_root(self) -> Path:
        return self.root / "backup"


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


def to_duckdb_code(symbol: str) -> str:
    value = symbol.strip().upper()
    if value.startswith("SHSE."):
        return "sh." + value[5:]
    if value.startswith("SZSE."):
        return "sz." + value[5:]
    raise ValueError(f"Unsupported symbol: {symbol}")


def to_gm_symbol(code: str) -> str:
    value = code.strip().lower()
    if value.startswith("sh."):
        return "SHSE." + value[3:]
    if value.startswith("sz."):
        return "SZSE." + value[3:]
    raise ValueError(f"Unsupported code: {code}")


def stock_output_path(paths: Paths, code: str) -> Path:
    return paths.daily_dir / f"{code.replace('.', '_')}.parquet"


def daily_basic_output_path(paths: Paths, code: str) -> Path:
    return paths.daily_basic_dir / f"{code.replace('.', '_')}.parquet"


def infer_board(code: str) -> str:
    value = code.lower()
    if value.startswith(("sh.688", "sh.689")):
        return "科创板"
    if value.startswith(("sz.300", "sz.301")):
        return "创业板"
    return "主板"


def load_instruments(limit: int) -> pd.DataFrame:
    rows = get_instruments(
        exchanges=EXCHANGES,
        sec_types=SEC_TYPES,
        skip_suspended=False,
        skip_st=True,
        fields="symbol,sec_name,exchange,listed_date,delisted_date",
        df=False,
    ) or []
    items = []
    today = date.today()
    for raw in rows:
        item = normalize_item(raw)
        symbol = str(item.get("symbol", ""))
        if not is_supported_ashare(symbol):
            continue
        delisted_date = parse_optional_date(item.get("delisted_date"))
        if delisted_date is not None and delisted_date <= today:
            continue
        code = to_duckdb_code(symbol)
        items.append({
            "code": code,
            "code_name": str(item.get("sec_name") or item.get("name") or code),
            "board": infer_board(code),
            "exchange": str(item.get("exchange", "")),
            "listed_date": item.get("listed_date"),
            "delisted_date": item.get("delisted_date"),
            "source": "eastmoney-quant",
        })

    frame = pd.DataFrame(items).drop_duplicates(subset=["code"], keep="last")
    frame = frame.sort_values("code").reset_index(drop=True)
    if limit > 0:
        frame = frame.head(limit)
    return frame


def parse_optional_date(value) -> date | None:
    if value in (None, "", "None"):
        return None
    try:
        parsed = pd.to_datetime(value)
        if pd.isna(parsed):
            return None
        return parsed.date()
    except Exception:
        return None


def is_supported_ashare(symbol: str) -> bool:
    value = symbol.strip().upper()
    return (
        value.startswith(("SHSE.600", "SHSE.601", "SHSE.603", "SHSE.605", "SHSE.688", "SHSE.689"))
        or value.startswith(("SZSE.000", "SZSE.001", "SZSE.002", "SZSE.003", "SZSE.300", "SZSE.301"))
    )


def load_last_bar_date(paths: Paths) -> date | None:
    if not paths.duckdb_path.exists():
        return None
    conn = duckdb.connect(str(paths.duckdb_path), read_only=True)
    try:
        value = conn.execute("SELECT max(date) FROM daily_bars").fetchone()[0]
        return pd.to_datetime(value).date() if value is not None else None
    finally:
        conn.close()


def load_last_weekly_bar_date(paths: Paths) -> date | None:
    if not paths.duckdb_path.exists():
        return None
    conn = duckdb.connect(str(paths.duckdb_path), read_only=True)
    try:
        table_exists = conn.execute(
            "SELECT count(*) FROM information_schema.tables WHERE table_name = 'weekly_bars'"
        ).fetchone()[0]
        if not table_exists:
            return None
        value = conn.execute("SELECT max(date) FROM weekly_bars").fetchone()[0]
        return pd.to_datetime(value).date() if value is not None else None
    finally:
        conn.close()


def is_weekly_current(last_date: date | None, last_weekly_date: date | None) -> bool:
    return last_date is not None and last_weekly_date is not None and last_weekly_date >= last_date


def normalize_daily(rows: list[dict], code_name: str, board: str) -> pd.DataFrame:
    if not rows:
        return pd.DataFrame()
    frame = pd.DataFrame(rows)
    frame["code"] = frame["symbol"].map(to_duckdb_code)
    frame["date"] = pd.to_datetime(frame["eob"]).dt.date
    frame["code_name"] = code_name
    frame["board"] = board
    frame["preclose"] = pd.to_numeric(frame.get("pre_close"), errors="coerce")
    for col in ["open", "high", "low", "close", "volume", "amount"]:
        frame[col] = pd.to_numeric(frame[col], errors="coerce")
    frame["adjustflag"] = "2"
    frame["turn"] = 0.0
    frame["tradestatus"] = 1
    frame["pctChg"] = ((frame["close"] / frame["preclose"]) - 1.0) * 100.0
    frame["peTTM"] = 0.0
    frame["pbMRQ"] = 0.0
    frame["psTTM"] = 0.0
    frame["pcfNcfTTM"] = 0.0
    frame["isST"] = 0
    return frame[
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
            "board",
            "code_name",
        ]
    ]


def normalize_daily_basic(rows: list[dict], code: str) -> pd.DataFrame:
    if not rows:
        return pd.DataFrame()

    frame = pd.DataFrame(rows)
    if "symbol" not in frame.columns:
        frame["symbol"] = to_gm_symbol(code)
    if "trade_date" not in frame.columns:
        for candidate in ["date", "pub_date", "rpt_date"]:
            if candidate in frame.columns:
                frame["trade_date"] = frame[candidate]
                break
    if "trade_date" not in frame.columns:
        return pd.DataFrame()

    frame["code"] = code
    frame["date"] = pd.to_datetime(frame["trade_date"], errors="coerce").dt.date
    for col in ["turnrate", "ttl_shr", "circ_shr", "ttl_shr_unl", "ttl_shr_ltd", "a_shr_unl"]:
        if col not in frame.columns:
            frame[col] = None
        frame[col] = pd.to_numeric(frame[col], errors="coerce")

    frame = frame.dropna(subset=["date"])
    if frame.empty:
        return pd.DataFrame()

    return frame[
        [
            "date",
            "code",
            "turnrate",
            "ttl_shr",
            "circ_shr",
            "ttl_shr_unl",
            "ttl_shr_ltd",
            "a_shr_unl",
        ]
    ]


def merge_daily_file(paths: Paths, incoming: pd.DataFrame, code: str) -> int:
    if incoming.empty:
        return 0
    path = stock_output_path(paths, code)
    if path.exists():
        existing = pd.read_parquet(path)
        existing["date"] = pd.to_datetime(existing["date"]).dt.date
        before = len(existing)
        merged = pd.concat([existing, incoming], ignore_index=True)
    else:
        before = 0
        merged = incoming
    merged = merged.drop_duplicates(subset=["code", "date"], keep="last")
    merged = merged.sort_values(["code", "date"]).reset_index(drop=True)
    merged.to_parquet(path, index=False)
    return max(0, len(merged) - before)


def merge_daily_basic_file(paths: Paths, incoming: pd.DataFrame, code: str) -> int:
    if incoming.empty:
        return 0
    path = daily_basic_output_path(paths, code)
    if path.exists():
        existing = pd.read_parquet(path)
        existing["date"] = pd.to_datetime(existing["date"]).dt.date
        before = len(existing)
        merged = pd.concat([existing, incoming], ignore_index=True)
    else:
        before = 0
        merged = incoming
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
        if paths.calendar_path.exists():
            conn.execute("CREATE OR REPLACE TABLE trade_calendar AS SELECT * FROM read_parquet(?)", [str(paths.calendar_path)])
        pattern = str(paths.daily_dir / "*.parquet")
        conn.execute("CREATE OR REPLACE TABLE daily_bars AS SELECT * FROM read_parquet(?)", [pattern])
        conn.execute("CREATE INDEX IF NOT EXISTS idx_daily_code_date ON daily_bars(code, date)")
        conn.execute("CREATE INDEX IF NOT EXISTS idx_daily_date ON daily_bars(date)")
        daily_basic_files = list(paths.daily_basic_dir.glob("*.parquet")) if paths.daily_basic_dir.exists() else []
        if daily_basic_files:
            basic_pattern = str(paths.daily_basic_dir / "*.parquet")
            conn.execute("CREATE OR REPLACE TABLE daily_basic AS SELECT * FROM read_parquet(?)", [basic_pattern])
            conn.execute("CREATE INDEX IF NOT EXISTS idx_daily_basic_code_date ON daily_basic(code, date)")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_daily_basic_date ON daily_basic(date)")
        conn.execute("""
            CREATE OR REPLACE TABLE weekly_bars AS
            WITH source AS (
                SELECT
                    *,
                    date_trunc('week', date) AS week_start
                FROM daily_bars
                WHERE adjustflag = '2'
                  AND tradestatus = 1
            ),
            ranked AS (
                SELECT
                    *,
                    row_number() OVER (PARTITION BY code, week_start ORDER BY date ASC) AS rn_asc,
                    row_number() OVER (PARTITION BY code, week_start ORDER BY date DESC) AS rn_desc
                FROM source
            ),
            weekly AS (
                SELECT
                    max(date) AS date,
                    code,
                    week_start,
                    max(CASE WHEN rn_asc = 1 THEN open END) AS open,
                    max(high) AS high,
                    min(low) AS low,
                    max(CASE WHEN rn_desc = 1 THEN close END) AS close,
                    max(CASE WHEN rn_asc = 1 THEN preclose END) AS preclose,
                    sum(volume) AS volume,
                    sum(amount) AS amount,
                    max(board) AS board,
                    max(code_name) AS code_name
                FROM ranked
                GROUP BY code, week_start
            )
            SELECT
                date,
                code,
                open,
                high,
                low,
                close,
                preclose,
                volume,
                amount,
                '2' AS adjustflag,
                0.0 AS turn,
                1 AS tradestatus,
                CASE
                    WHEN preclose > 0 THEN (close / preclose - 1) * 100
                    ELSE 0
                END AS pctChg,
                0.0 AS peTTM,
                0.0 AS pbMRQ,
                0.0 AS psTTM,
                0.0 AS pcfNcfTTM,
                0 AS isST,
                board,
                code_name
            FROM weekly
            WHERE open IS NOT NULL
              AND high IS NOT NULL
              AND low IS NOT NULL
              AND close IS NOT NULL;
            """)
        conn.execute("CREATE INDEX IF NOT EXISTS idx_weekly_code_date ON weekly_bars(code, date)")
        conn.execute("CREATE INDEX IF NOT EXISTS idx_weekly_date ON weekly_bars(date)")
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


def backup_existing(paths: Paths) -> Path | None:
    if not paths.duckdb_path.exists() and not paths.daily_dir.exists():
        return None
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    target = paths.backup_root / f"history-before-eastmoney-{stamp}"
    target.mkdir(parents=True, exist_ok=False)
    if paths.duckdb_path.exists():
        shutil.copy2(paths.duckdb_path, target / paths.duckdb_path.name)
    if paths.daily_dir.exists():
        shutil.copytree(paths.daily_dir, target / "daily_bars")
    if paths.daily_basic_dir.exists():
        shutil.copytree(paths.daily_basic_dir, target / "daily_basic")
    return target


def clear_daily(paths: Paths) -> None:
    if paths.daily_dir.exists():
        shutil.rmtree(paths.daily_dir)
    if paths.daily_basic_dir.exists():
        shutil.rmtree(paths.daily_basic_dir)
    paths.daily_dir.mkdir(parents=True, exist_ok=True)
    paths.daily_basic_dir.mkdir(parents=True, exist_ok=True)
    if paths.duckdb_path.exists():
        paths.duckdb_path.unlink()


def download_history(
    paths: Paths,
    universe: pd.DataFrame,
    start: date,
    end: date,
    sleep_seconds: float,
    adjustflag: str,
) -> tuple[int, int]:
    touched = 0
    inserted_rows = 0
    for index, row in enumerate(universe.to_dict("records"), start=1):
        symbol = to_gm_symbol(str(row["code"]))
        print(f"[history-update] {index}/{len(universe)} {symbol} {row.get('code_name', '')}", flush=True)
        rows = []
        if start <= end:
            rows = history(
                symbol=symbol,
                frequency="1d",
                start_time=f"{start.isoformat()} 00:00:00",
                end_time=f"{end.isoformat()} 23:59:59",
                fields=FIELDS,
                skip_suspended=True,
                adjust=ADJUST_PREV,
                df=False,
            ) or []
        normalized = normalize_daily(
            [normalize_item(item) for item in rows],
            str(row.get("code_name", "")),
            str(row.get("board", "")),
        )
        inserted = merge_daily_file(paths, normalized, str(row["code"]))
        inserted_rows += inserted
        if inserted > 0:
            touched += 1
        if sleep_seconds > 0:
            time.sleep(sleep_seconds)
    return touched, inserted_rows


def download_daily_basic(
    paths: Paths,
    universe: pd.DataFrame,
    start: date,
    end: date,
    sleep_seconds: float,
) -> tuple[int, int]:
    if stk_get_daily_basic is None:
        print("[history-update:daily-basic] stk_get_daily_basic is unavailable in current gm SDK; skip turnrate download.", flush=True)
        return 0, 0

    touched = 0
    inserted_rows = 0
    fields = "turnrate,ttl_shr,circ_shr,ttl_shr_unl,ttl_shr_ltd,a_shr_unl"
    for index, row in enumerate(universe.to_dict("records"), start=1):
        code = str(row["code"])
        symbol = to_gm_symbol(code)
        print(f"[history-update:daily-basic] {index}/{len(universe)} {symbol}", flush=True)
        try:
            rows = stk_get_daily_basic(
                symbol=symbol,
                fields=fields,
                start_date=start.isoformat(),
                end_date=end.isoformat(),
                df=False,
            ) or []
        except TypeError:
            rows = stk_get_daily_basic(symbol, fields, start.isoformat(), end.isoformat(), False) or []

        normalized = normalize_daily_basic([normalize_item(item) for item in rows], code)
        inserted = merge_daily_basic_file(paths, normalized, code)
        inserted_rows += inserted
        if inserted > 0:
            touched += 1
        if sleep_seconds > 0:
            time.sleep(sleep_seconds)
    return touched, inserted_rows


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Update A-share historical daily bars from EastMoney Quant SDK.")
    parser.add_argument("--data-dir", required=True)
    parser.add_argument("--start", default="2015-01-01")
    parser.add_argument("--end", default=date.today().isoformat())
    parser.add_argument("--adjustflag", default="2")
    parser.add_argument("--limit", type=int, default=0)
    parser.add_argument("--rebuild", action="store_true")
    parser.add_argument("--no-backup", action="store_true")
    parser.add_argument("--sleep-seconds", type=float, default=0.03)
    parser.add_argument("--include-weekly", action="store_true")
    parser.add_argument("--dry-run", action="store_true")
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    token = os.environ.get("EASTMONEY_QUANT_TOKEN", "").strip()
    if not token:
        raise RuntimeError("EASTMONEY_QUANT_TOKEN is required.")

    paths = Paths(Path(args.data_dir).resolve())
    paths.parquet.mkdir(parents=True, exist_ok=True)
    paths.daily_dir.mkdir(parents=True, exist_ok=True)
    paths.daily_basic_dir.mkdir(parents=True, exist_ok=True)
    set_token(token)

    end = pd.to_datetime(args.end).date()
    last_date = load_last_bar_date(paths)
    start = pd.to_datetime(args.start).date() if args.rebuild or last_date is None else last_date + timedelta(days=1)
    last_weekly_date = load_last_weekly_bar_date(paths) if args.include_weekly else None
    weekly_current = not args.include_weekly or is_weekly_current(last_date, last_weekly_date)

    print(
        f"[history-update] provider=eastmoney-quant start={start} daily_latest={last_date} weekly_latest={last_weekly_date if args.include_weekly else 'disabled'} weekly_mode=aggregate-daily end={end} rebuild={args.rebuild}",
        flush=True,
    )

    if args.dry_run:
        return 0
    if start > end and weekly_current:
        print(f"[history-update] no calendar gap. daily_latest={last_date} weekly_latest={last_weekly_date if args.include_weekly else 'disabled'} end={end}", flush=True)
        return 0
    if start > end and not weekly_current:
        print(f"[history-update] no daily gap, rebuilding weekly_bars from existing daily_bars. daily_latest={last_date} weekly_latest={last_weekly_date} end={end}", flush=True)
        rebuild_duckdb(paths)
        refreshed_weekly_date = load_last_weekly_bar_date(paths)
        print(
            f"[history-update] completed. touched_stocks=0 inserted_rows=0 "
            f"weekly_mode=aggregate-daily from=existing-daily weekly_latest={refreshed_weekly_date} to={end} duckdb={paths.duckdb_path}",
            flush=True,
        )
        return 0

    universe = load_instruments(args.limit)
    if universe.empty:
        raise RuntimeError("EastMoney Quant returned no A-share instruments.")
    print(f"[history-update] universe={len(universe)} daily_download_start={start} end={end}", flush=True)

    backup = None if args.no_backup else backup_existing(paths)
    if backup is not None:
        print(f"[history-update] backup={backup}", flush=True)
    if args.rebuild:
        clear_daily(paths)

    universe.to_parquet(paths.universe_path, index=False)
    touched, inserted_rows = download_history(
        paths,
        universe,
        start,
        end,
        args.sleep_seconds,
        args.adjustflag,
    )
    basic_touched, basic_inserted_rows = download_daily_basic(
        paths,
        universe,
        start,
        end,
        args.sleep_seconds,
    )
    rebuild_duckdb(paths)
    print(
        f"[history-update] completed. touched_stocks={touched} inserted_rows={inserted_rows} "
        f"daily_basic_touched={basic_touched} daily_basic_rows={basic_inserted_rows} "
        f"weekly_mode=aggregate-daily from={start} to={end} duckdb={paths.duckdb_path}",
        flush=True,
    )
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as exc:
        print(f"[history-update:error] {type(exc).__name__}: {exc}", file=sys.stderr, flush=True)
        raise
