from __future__ import annotations

import argparse
import csv
import datetime as dt
import re
import sys
from pathlib import Path


LOCAL_PACKAGE_DIR = Path(__file__).resolve().parents[1] / "history_update" / ".python_packages"
if LOCAL_PACKAGE_DIR.exists():
    sys.path.insert(0, str(LOCAL_PACKAGE_DIR))

import baostock as bs  # noqa: E402


DEFAULT_OUTPUT = (
    Path(__file__).resolve().parents[2]
    / "src"
    / "AShareRadar.ServiceHost"
    / "data"
    / "sector-mapping.csv"
)

INDUSTRY_CODE_OVERRIDES = {
    "银行": "bank",
    "证券": "broker",
    "保险": "insurance",
    "房地产": "real-estate",
    "食品饮料": "liquor-food",
    "医药生物": "medicine",
    "电力设备": "new-energy",
    "汽车": "auto",
    "电子": "semiconductor",
    "计算机": "software-ai",
    "通信": "telecom",
    "国防军工": "military",
    "公用事业": "power",
    "煤炭": "coal-metal",
    "有色金属": "coal-metal",
    "钢铁": "coal-metal",
    "基础化工": "chemical",
    "建筑装饰": "construction",
    "机械设备": "machinery",
    "商贸零售": "consumer",
    "农林牧渔": "agriculture",
    "交通运输": "transport",
    "环保": "environment",
}


def normalize_symbol(code: str) -> str:
    return code.replace("sh.", "").replace("sz.", "").strip()


def build_sector_code(industry: str) -> str:
    if industry in INDUSTRY_CODE_OVERRIDES:
        return INDUSTRY_CODE_OVERRIDES[industry]

    normalized = re.sub(r"[^0-9A-Za-z]+", "-", industry).strip("-").lower()
    return normalized or "unknown"


def fetch_industry_rows(date: str | None) -> list[dict[str, str]]:
    login = bs.login()
    if login.error_code != "0":
        raise RuntimeError(f"baostock login failed: {login.error_code} {login.error_msg}")

    try:
        rs = bs.query_stock_industry(date=date) if date else bs.query_stock_industry()
        if rs.error_code != "0":
            raise RuntimeError(f"query_stock_industry failed: {rs.error_code} {rs.error_msg}")

        rows: list[dict[str, str]] = []
        while rs.next():
            item = dict(zip(rs.fields, rs.get_row_data()))
            symbol = normalize_symbol(item.get("code", ""))
            industry = item.get("industry", "").strip()
            if not symbol or not industry:
                continue

            rows.append(
                {
                    "symbol": symbol,
                    "sector_code": build_sector_code(industry),
                    "sector_name": industry,
                    "source": item.get("industryClassification", "baostock") or "baostock",
                    "updated_at": dt.date.today().isoformat(),
                }
            )

        return rows
    finally:
        bs.logout()


def write_csv(path: Path, rows: list[dict[str, str]]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("w", newline="", encoding="utf-8-sig") as handle:
        writer = csv.DictWriter(
            handle,
            fieldnames=["symbol", "sector_code", "sector_name", "source", "updated_at"],
        )
        writer.writeheader()
        writer.writerows(sorted(rows, key=lambda item: item["symbol"]))


def main() -> int:
    parser = argparse.ArgumentParser(description="Update A-share sector mapping from baostock.")
    parser.add_argument("--date", default=None, help="Optional baostock query date, yyyy-mm-dd.")
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    args = parser.parse_args()

    rows = fetch_industry_rows(args.date)
    write_csv(args.output, rows)
    print(f"wrote {len(rows)} sector mappings to {args.output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
