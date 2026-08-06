from __future__ import annotations

import json
import os
import sys
from datetime import datetime, timedelta
from decimal import Decimal
from typing import Any, Iterable

import akshare as ak  # type: ignore
from gm.api import (  # type: ignore
    current,
    get_instruments,
    set_token,
)


INDEX_SYMBOLS = {
    "IF": ("SHSE.000300", Decimal("0.35")),
    "IH": ("SHSE.000016", Decimal("0.20")),
    "IC": ("SHSE.000905", Decimal("0.25")),
    "IM": ("SHSE.000852", Decimal("0.20")),
}

OPTION_EXCHANGES = ("SHSE", "SZSE")
CALL_FLAGS = ("call", "CALL", "C")
PUT_FLAGS = ("put", "PUT", "P")


def main() -> int:
    token = os.environ.get("EASTMONEY_QUANT_TOKEN", "").strip()
    if not token:
        log("EASTMONEY_QUANT_TOKEN is not set.")
        emit(None, None)
        return 0

    set_token(token)
    index_future_basis = safe_call(load_index_future_basis, "index_future_basis")
    option_pcr = safe_call(load_option_pcr, "option_pcr")
    emit(index_future_basis, option_pcr)
    return 0


def load_index_future_basis() -> Decimal | None:
    basis_items: list[tuple[Decimal, Decimal]] = []
    for product_code, (index_symbol, weight) in INDEX_SYMBOLS.items():
        future_symbol = resolve_future_symbol(product_code)
        if not future_symbol:
            log(f"{product_code} continuous contract not found.")
            continue

        prices = load_prices([future_symbol, index_symbol])
        future_price = prices.get(future_symbol)
        index_price = prices.get(index_symbol)
        if future_price is None or index_price is None:
            log(f"{product_code} price missing. future={future_symbol} index={index_symbol}")
            continue

        basis_items.append((future_price - index_price, weight))

    if not basis_items:
        return None

    total_weight = sum((item[1] for item in basis_items), Decimal("0"))
    if total_weight <= 0:
        return None

    return round_decimal(sum((basis * weight for basis, weight in basis_items), Decimal("0")) / total_weight)


def resolve_future_symbol(product_code: str) -> str | None:
    today = datetime.now().astimezone()
    rows = normalize_rows(get_instruments(
        exchanges="CFFEX",
        sec_types=4,
        fields="symbol,sec_name,listed_date,delisted_date",
        df=False,
    ))
    candidates = []
    for row in rows:
        symbol = str(first_value(row, "symbol") or "").strip()
        sec_name = str(first_value(row, "sec_name") or symbol.split(".")[-1]).strip()
        listed_date = first_value(row, "listed_date")
        delisted_date = first_value(row, "delisted_date")
        if not symbol or not sec_name.startswith(product_code):
            continue
        if listed_date is not None and today < listed_date:
            continue
        if delisted_date is not None and today > delisted_date:
            continue
        candidates.append((delisted_date or today, symbol))

    candidates.sort(key=lambda item: item[0])
    return candidates[0][1] if candidates else None


def load_option_pcr() -> Decimal | None:
    sse_pcr = load_option_pcr_from_sse_stats()
    if sse_pcr is not None:
        return sse_pcr

    calls = load_option_symbols(CALL_FLAGS)
    puts = load_option_symbols(PUT_FLAGS)
    if not calls or not puts:
        log(f"option symbols missing. calls={len(calls)} puts={len(puts)}")
        return None

    call_quotes = load_quote_rows(calls)
    put_quotes = load_quote_rows(puts)
    call_volume = sum_decimal(call_quotes, "cum_volume", "volume")
    put_volume = sum_decimal(put_quotes, "cum_volume", "volume")
    call_position = sum_decimal(call_quotes, "cum_position", "position")
    put_position = sum_decimal(put_quotes, "cum_position", "position")

    volume_pcr = put_volume / call_volume if call_volume > 0 else None
    position_pcr = put_position / call_position if call_position > 0 else None
    if volume_pcr is None and position_pcr is None:
        return None
    if volume_pcr is None:
        return round_decimal(position_pcr)
    if position_pcr is None:
        return round_decimal(volume_pcr)

    return round_decimal(volume_pcr * Decimal("0.7") + position_pcr * Decimal("0.3"))


def load_option_pcr_from_sse_stats() -> Decimal | None:
    for offset in range(0, 10):
        query_date = (datetime.now() - timedelta(days=offset)).strftime("%Y%m%d")
        try:
            rows = normalize_rows(ak.option_daily_stats_sse(date=query_date))
        except Exception as exc:
            log(f"option_daily_stats_sse({query_date}) failed: {exc}")
            continue

        put_volume = Decimal("0")
        call_volume = Decimal("0")
        for row in rows:
            put_value = to_decimal(first_value(row, "认沽成交量"))
            call_value = to_decimal(first_value(row, "认购成交量"))
            if put_value is not None:
                put_volume += put_value
            if call_value is not None:
                call_volume += call_value

        if put_volume > 0 and call_volume > 0:
            log(f"option PCR loaded from SSE stats date={query_date}.")
            return round_decimal(put_volume / call_volume)

    return None


def load_option_symbols(flags: Iterable[str]) -> list[str]:
    rows = normalize_rows(get_instruments(
        sec_types=5,
        fields="symbol,sec_name,listed_date,delisted_date",
        df=False,
    ))
    if not rows:
        log("option instruments are unavailable from get_instruments(sec_types=5).")
        return []

    today = datetime.now().astimezone()
    flag_values = tuple(item.upper() for item in flags)
    symbols: list[str] = []
    for row in rows:
        symbol = str(first_value(row, "symbol") or "").strip()
        name = str(first_value(row, "sec_name") or "").upper()
        listed_date = first_value(row, "listed_date")
        delisted_date = first_value(row, "delisted_date")
        if not symbol:
            continue
        if listed_date is not None and today < listed_date:
            continue
        if delisted_date is not None and today > delisted_date:
            continue
        if any(flag in symbol.upper() or flag in name for flag in flag_values):
            symbols.append(symbol)

    return sorted(set(symbols))


def load_prices(symbols: list[str]) -> dict[str, Decimal]:
    rows = load_quote_rows(symbols)
    result: dict[str, Decimal] = {}
    for row in rows:
        symbol = str(first_value(row, "symbol") or "").strip()
        price = to_decimal(first_value(row, "price", "close"))
        if symbol and price is not None:
            result[symbol] = price
    return result


def load_quote_rows(symbols: list[str]) -> list[Any]:
    rows: list[Any] = []
    for batch in chunks(symbols, 200):
        try:
            rows.extend(normalize_rows(current(batch, fields="symbol,price,cum_volume,cum_position")))
        except Exception as exc:
            log(f"current batch failed: {exc}")
    return rows


def sum_decimal(rows: Iterable[Any], *fields: str) -> Decimal:
    total = Decimal("0")
    for row in rows:
        value = to_decimal(first_value(row, *fields))
        if value is not None:
            total += value
    return total


def safe_call(func: Any, name: str) -> Decimal | None:
    try:
        return func()
    except Exception as exc:
        log(f"{name} failed: {exc}")
        return None


def normalize_rows(rows: Any) -> list[Any]:
    if rows is None:
        return []
    if hasattr(rows, "to_dict"):
        return rows.to_dict("records")
    if isinstance(rows, dict):
        return [rows]
    return list(rows)


def first_value(row: Any, *names: str) -> Any:
    for name in names:
        if isinstance(row, dict) and name in row:
            return row[name]
        if hasattr(row, name):
            return getattr(row, name)
    return None


def to_decimal(value: Any) -> Decimal | None:
    if value is None:
        return None
    try:
        return Decimal(str(value))
    except Exception:
        return None


def round_decimal(value: Decimal | None) -> Decimal | None:
    return value.quantize(Decimal("0.0001")) if value is not None else None


def chunks(values: list[str], size: int) -> Iterable[list[str]]:
    for index in range(0, len(values), size):
        yield values[index : index + size]


def emit(index_future_basis: Decimal | None, option_pcr: Decimal | None) -> None:
    payload = {
        "IndexFutureBasis": str(index_future_basis) if index_future_basis is not None else None,
        "OptionPcr": str(option_pcr) if option_pcr is not None else None,
    }
    print("[external-sentiment-json]" + json.dumps(payload, ensure_ascii=True), file=sys.stderr, flush=True)


def log(message: str) -> None:
    print("[external-sentiment] " + message, file=sys.stderr, flush=True)


if __name__ == "__main__":
    raise SystemExit(main())
