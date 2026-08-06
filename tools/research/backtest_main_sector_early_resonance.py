#!/usr/bin/env python3
"""Prototype backtest for an earlier main-sector resonance trigger.

This research script does not register a production strategy. It uses the
existing ServiceHost APIs to compare:
  1. the current main-sector-resonance first hit, and
  2. a proposed intraday early trigger based on 1-minute bars.

Universe limitation:
The script uses symbols that were already hit by main-sector-resonance on the
selected trading date, because the runtime API does not expose historical
minute-by-minute sector/concept heat snapshots for the entire market.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import statistics
import sys
import urllib.parse
import urllib.request
from dataclasses import dataclass
from datetime import date, datetime
from pathlib import Path
from typing import Any


DEFAULT_BASE_URL = "http://127.0.0.1:18730"
MAIN_STRATEGY_CODE = "main-sector-resonance"


@dataclass(frozen=True)
class Bar:
    time: datetime
    open: float
    high: float
    low: float
    close: float
    volume: float


@dataclass(frozen=True)
class Hit:
    symbol: str
    name: str
    time: datetime
    price: float
    score: float
    events: int


@dataclass(frozen=True)
class Trigger:
    symbol: str
    name: str
    time: datetime
    price: float
    score: float
    stage: str
    vwap: float
    day_high: float
    high_position_pct: float
    drawdown_from_high_pct: float
    volume_accel_5m: float
    return_5m_pct: float
    platform_breakout_pct: float
    reason: str


def fetch_json(base_url: str, path: str, query: dict[str, Any] | None = None) -> Any:
    url = base_url.rstrip("/") + path
    if query:
        url += "?" + urllib.parse.urlencode(query)
    with urllib.request.urlopen(url, timeout=30) as response:
        return json.loads(response.read().decode("utf-8"))


def parse_time(value: str) -> datetime:
    return datetime.fromisoformat(value)


def parse_bars(payload: list[dict[str, Any]]) -> list[Bar]:
    bars: list[Bar] = []
    for item in payload:
        raw_time = item.get("tradingTime") or item.get("time")
        if not raw_time:
            continue
        try:
            bars.append(
                Bar(
                    time=parse_time(str(raw_time)),
                    open=float(item.get("open") or 0),
                    high=float(item.get("high") or 0),
                    low=float(item.get("low") or 0),
                    close=float(item.get("close") or 0),
                    volume=float(item.get("volume") or 0),
                )
            )
        except (TypeError, ValueError):
            continue
    return [bar for bar in bars if bar.close > 0 and bar.high > 0 and bar.low > 0]


def pct(value: float) -> float:
    return value * 100.0


def safe_avg(values: list[float]) -> float:
    return sum(values) / len(values) if values else 0.0


def find_main_hits(base_url: str, trading_date: str) -> dict[str, Hit]:
    signals = fetch_json(
        base_url,
        "/api/history/signals",
        {"tradingDate": trading_date, "count": 10000},
    )
    grouped: dict[str, list[dict[str, Any]]] = {}
    for signal in signals:
        if signal.get("strategyCode") != MAIN_STRATEGY_CODE:
            continue
        symbol = str(signal.get("symbol") or "").strip()
        if symbol:
            grouped.setdefault(symbol, []).append(signal)

    hits: dict[str, Hit] = {}
    for symbol, items in grouped.items():
        ordered = sorted(items, key=lambda item: str(item.get("eventTime") or ""))
        first = ordered[0]
        hits[symbol] = Hit(
            symbol=symbol,
            name=str(first.get("name") or ""),
            time=parse_time(str(first.get("eventTime"))),
            price=float(first.get("price") or 0),
            score=float(first.get("score") or 0),
            events=len(items),
        )
    return hits


def load_minute_bars(base_url: str, symbol: str, trading_date: date) -> list[Bar]:
    payload = fetch_json(
        base_url,
        "/api/market-data/kline",
        {"symbol": symbol, "period": "1m", "count": 360},
    )
    bars = parse_bars(payload)
    bars = [bar for bar in bars if bar.time.date() == trading_date]
    return sorted(bars, key=lambda item: item.time)


def compute_vwap(bars: list[Bar], index: int) -> float:
    turnover = 0.0
    volume = 0.0
    for bar in bars[: index + 1]:
        typical_price = (bar.high + bar.low + bar.close) / 3.0
        turnover += typical_price * bar.volume
        volume += bar.volume
    return turnover / volume if volume > 0 else bars[index].close


def first_early_trigger(symbol: str, name: str, bars: list[Bar], latest_time: datetime | None = None) -> Trigger | None:
    if len(bars) < 35:
        return None

    day_open = bars[0].open
    day_high = bars[0].high
    for index, bar in enumerate(bars):
        if latest_time is not None and bar.time >= latest_time:
            break

        day_high = max(day_high, bar.high)
        if index < 25 or day_open <= 0:
            continue

        previous_15 = bars[max(0, index - 15) : index]
        previous_20 = bars[max(0, index - 25) : max(0, index - 5)]
        last_5 = bars[index - 4 : index + 1]
        if len(previous_15) < 10 or len(previous_20) < 10 or len(last_5) < 5:
            continue

        vwap = compute_vwap(bars, index)
        high_position = bar.close / day_high if day_high > 0 else 1.0
        drawdown_from_high = (day_high - bar.close) / day_high if day_high > 0 else 0.0
        return_from_open = (bar.close / day_open - 1.0) if day_open > 0 else 0.0
        return_5m = (bar.close / last_5[0].open - 1.0) if last_5[0].open > 0 else 0.0
        volume_5m = sum(item.volume for item in last_5)
        avg_previous_5m_volume = sum(item.volume for item in previous_20) / 4.0
        volume_accel = volume_5m / avg_previous_5m_volume if avg_previous_5m_volume > 0 else 0.0
        platform_high = max(item.high for item in previous_15)
        platform_breakout = (bar.close / platform_high - 1.0) if platform_high > 0 else 0.0

        base_watch = (
            return_from_open >= 0.003
            and return_from_open <= 0.065
            and bar.close >= vwap
            and drawdown_from_high <= 0.012
        )
        candidate = (
            base_watch
            and high_position <= 0.992
            and volume_accel >= 1.35
            and return_5m >= 0.003
        )
        breakout_confirm = (
            base_watch
            and platform_breakout >= 0.003
            and volume_accel >= 1.5
            and high_position <= 0.998
        )

        if not candidate and not breakout_confirm:
            continue

        high_penalty = 0.0
        if high_position >= 0.99:
            high_penalty = 25.0
        elif high_position >= 0.98:
            high_penalty = 15.0
        elif high_position >= 0.97:
            high_penalty = 8.0

        drawdown_penalty = 0.0
        if drawdown_from_high >= 0.02:
            continue
        if drawdown_from_high >= 0.012:
            drawdown_penalty = 12.0
        elif drawdown_from_high >= 0.008:
            drawdown_penalty = 6.0

        score = round(
            58
            + pct(return_from_open) * 2.0
            + min(volume_accel * 4.0, 12.0)
            + max(pct(return_5m), 0) * 1.8
            + max(pct(platform_breakout), 0) * 3.0
            - high_penalty
            - drawdown_penalty,
            2,
        )
        stage = "Confirm" if breakout_confirm else "Candidate"
        reason = (
            f"{stage}: close>=VWAP, 5m volume accel {volume_accel:.2f}, "
            f"5m return {pct(return_5m):.2f}%, high position {pct(high_position):.2f}%"
        )
        return Trigger(
            symbol=symbol,
            name=name,
            time=bar.time,
            price=bar.close,
            score=score,
            stage=stage,
            vwap=vwap,
            day_high=day_high,
            high_position_pct=pct(high_position),
            drawdown_from_high_pct=pct(drawdown_from_high),
            volume_accel_5m=volume_accel,
            return_5m_pct=pct(return_5m),
            platform_breakout_pct=pct(platform_breakout),
            reason=reason,
        )
    return None


def evaluate_entry(bars: list[Bar], entry_time: datetime, entry_price: float) -> dict[str, float | str | None]:
    after = [bar for bar in bars if bar.time >= entry_time]
    if not after or entry_price <= 0:
        return {
            "max_gain_pct": None,
            "final_gain_pct": None,
            "drawdown_from_high_pct": None,
            "stop_loss_pct": None,
            "stop_hit_time": None,
        }

    max_high = max(bar.high for bar in after)
    final_close = after[-1].close
    stop_loss = entry_price * 0.985
    stop_hit = next((bar for bar in after if bar.low <= stop_loss), None)
    return {
        "max_gain_pct": pct(max_high / entry_price - 1.0),
        "final_gain_pct": pct(final_close / entry_price - 1.0),
        "drawdown_from_high_pct": pct((max_high - final_close) / max_high) if max_high > 0 else None,
        "stop_loss_pct": pct(stop_loss / entry_price - 1.0),
        "stop_hit_time": stop_hit.time.isoformat() if stop_hit else None,
    }


def summarize(values: list[float]) -> dict[str, float | None]:
    if not values:
        return {"avg": None, "median": None}
    return {
        "avg": round(statistics.mean(values), 4),
        "median": round(statistics.median(values), 4),
    }


def build_summary(rows: list[dict[str, Any]]) -> dict[str, Any]:
    valid = [row for row in rows if row.get("early_final_gain_pct") is not None]
    baseline_valid = [row for row in rows if row.get("baseline_final_gain_pct") is not None]
    return {
        "symbols": len(rows),
        "early_triggered": len(valid),
        "baseline_valid": len(baseline_valid),
        "early_final_positive": sum(1 for row in valid if row["early_final_gain_pct"] > 0),
        "baseline_final_positive": sum(1 for row in baseline_valid if row["baseline_final_gain_pct"] > 0),
        "early_max_gain_ge_1pct": sum(1 for row in valid if row["early_max_gain_pct"] >= 1),
        "baseline_max_gain_ge_1pct": sum(1 for row in baseline_valid if row["baseline_max_gain_pct"] >= 1),
        "early_avg_final_gain_pct": summarize([row["early_final_gain_pct"] for row in valid])["avg"],
        "baseline_avg_final_gain_pct": summarize([row["baseline_final_gain_pct"] for row in baseline_valid])["avg"],
        "early_avg_max_gain_pct": summarize([row["early_max_gain_pct"] for row in valid])["avg"],
        "baseline_avg_max_gain_pct": summarize([row["baseline_max_gain_pct"] for row in baseline_valid])["avg"],
        "early_avg_high_position_pct": summarize([row["early_final_day_high_position_pct"] for row in valid])["avg"],
        "baseline_avg_high_position_pct": summarize([row["baseline_high_position_pct"] for row in baseline_valid])["avg"],
    }


def write_csv(path: Path, rows: list[dict[str, Any]]) -> None:
    if not rows:
        path.write_text("", encoding="utf-8")
        return
    with path.open("w", encoding="utf-8-sig", newline="") as file:
        writer = csv.DictWriter(file, fieldnames=list(rows[0].keys()))
        writer.writeheader()
        writer.writerows(rows)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--date", default=datetime.now().strftime("%Y-%m-%d"))
    parser.add_argument("--base-url", default=DEFAULT_BASE_URL)
    parser.add_argument("--output-dir", default=None)
    args = parser.parse_args()

    output_dir = Path(args.output_dir) if args.output_dir else Path("artifacts") / f"research-main-sector-early-{args.date}"
    output_dir.mkdir(parents=True, exist_ok=True)

    trading_date = date.fromisoformat(args.date)
    hits = find_main_hits(args.base_url, args.date)
    if not hits:
        print(f"No {MAIN_STRATEGY_CODE} signals found for {args.date}.", file=sys.stderr)
        return 2

    rows: list[dict[str, Any]] = []
    for index, hit in enumerate(hits.values(), start=1):
        try:
            bars = load_minute_bars(args.base_url, hit.symbol, trading_date)
        except Exception as exc:
            print(f"[warn] failed to load kline for {hit.symbol}: {exc}", file=sys.stderr)
            continue

        if not bars:
            continue

        baseline_eval = evaluate_entry(bars, hit.time, hit.price)
        day_high = max(bar.high for bar in bars)
        baseline_high_position = pct(hit.price / day_high) if day_high > 0 and hit.price > 0 else None
        trigger = first_early_trigger(hit.symbol, hit.name, bars, hit.time)
        if trigger:
            early_eval = evaluate_entry(bars, trigger.time, trigger.price)
        else:
            early_eval = {
                "max_gain_pct": None,
                "final_gain_pct": None,
                "drawdown_from_high_pct": None,
                "stop_loss_pct": None,
                "stop_hit_time": None,
            }

        rows.append(
            {
                "symbol": hit.symbol,
                "name": hit.name,
                "events": hit.events,
                "baseline_time": hit.time.isoformat(),
                "baseline_price": round(hit.price, 4),
                "baseline_score": round(hit.score, 4),
                "baseline_high_position_pct": round(baseline_high_position, 4) if baseline_high_position is not None else None,
                "baseline_max_gain_pct": round(baseline_eval["max_gain_pct"], 4) if baseline_eval["max_gain_pct"] is not None else None,
                "baseline_final_gain_pct": round(baseline_eval["final_gain_pct"], 4) if baseline_eval["final_gain_pct"] is not None else None,
                "early_triggered": trigger is not None,
                "early_stage": trigger.stage if trigger else None,
                "early_time": trigger.time.isoformat() if trigger else None,
                "early_price": round(trigger.price, 4) if trigger else None,
                "early_score": round(trigger.score, 4) if trigger else None,
                "early_minutes_before_baseline": round((hit.time - trigger.time).total_seconds() / 60, 2) if trigger else None,
                "early_high_position_pct": round(trigger.high_position_pct, 4) if trigger else None,
                "early_final_day_high_position_pct": round(trigger.price / day_high * 100.0, 4) if trigger and day_high > 0 else None,
                "early_drawdown_from_high_pct": round(trigger.drawdown_from_high_pct, 4) if trigger else None,
                "early_volume_accel_5m": round(trigger.volume_accel_5m, 4) if trigger else None,
                "early_return_5m_pct": round(trigger.return_5m_pct, 4) if trigger else None,
                "early_platform_breakout_pct": round(trigger.platform_breakout_pct, 4) if trigger else None,
                "early_max_gain_pct": round(early_eval["max_gain_pct"], 4) if early_eval["max_gain_pct"] is not None else None,
                "early_final_gain_pct": round(early_eval["final_gain_pct"], 4) if early_eval["final_gain_pct"] is not None else None,
                "early_stop_hit_time": early_eval["stop_hit_time"],
                "early_reason": trigger.reason if trigger else None,
            }
        )
        if index % 25 == 0:
            print(f"processed {index}/{len(hits)} symbols")

    rows.sort(key=lambda row: (not row["early_triggered"], row["symbol"]))
    summary = build_summary(rows)
    summary_path = output_dir / "summary.json"
    detail_path = output_dir / "detail.csv"
    summary_path.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    write_csv(detail_path, rows)

    print(json.dumps(summary, ensure_ascii=False, indent=2))
    print(f"detail_csv={detail_path}")
    print(f"summary_json={summary_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
