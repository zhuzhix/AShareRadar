from __future__ import annotations

import argparse
import json
import os
import sys
from datetime import datetime
from pathlib import Path
from typing import Any

import numpy as np
import pandas as pd


PROJECT_ROOT = Path(r"C:\Users\Administrator\Documents\QuantResearch\qlib-factor-platform")
SHARED_ROOT = Path(r"C:\Users\Administrator\Documents\QuantResearch\shared_data")
DEFAULT_OUTPUT_ROOT = Path(r"C:\Users\Administrator\Documents\Codex\2026-08-01\zhi-x\next_day_direction_outputs")

STRATEGY_CODE = "qlib-next-day-direction"
STRATEGY_NAME = "\u660e\u65e5\u9884\u6d4b"
LABEL_EXPR = "Ref($close, -1) / $close - 1"
LABEL_DESCRIPTION = "今天收盘买入，明天收盘卖出的收益"

for import_path in [PROJECT_ROOT / "src", PROJECT_ROOT]:
    if str(import_path) not in sys.path:
        sys.path.insert(0, str(import_path))


def latest_shared_data_date() -> pd.Timestamp:
    path = SHARED_ROOT / "parquet" / "daily_bars.parquet"
    if not path.exists():
        raise FileNotFoundError(f"Shared daily bars not found: {path}")
    frame = pd.read_parquet(path, columns=["trade_date"])
    latest = pd.to_datetime(frame["trade_date"], errors="coerce").max()
    if pd.isna(latest):
        raise RuntimeError("Shared daily bars contains no valid trade_date.")
    return pd.Timestamp(latest).normalize()


def write_json(path: Path, payload: dict[str, Any]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2, default=str), encoding="utf-8")


def write_progress(path: Path, **payload: Any) -> None:
    write_json(path, {"updated_at": datetime.now().isoformat(timespec="seconds"), **payload})


def normalize_symbol(value: str) -> str:
    text = value.strip().upper()
    if not text:
        return ""
    text = text.replace("_", ".")
    if text.startswith(("SH", "SZ")) and len(text) >= 8:
        code = text[2:8]
        exchange = text[:2]
        return f"{code}.{exchange}"
    if "." in text:
        code, exchange = text.split(".", 1)
        exchange = exchange[:2]
        return f"{code.zfill(6)}.{exchange}"
    code = text.zfill(6)
    exchange = "SH" if code.startswith(("5", "6", "9")) else "SZ"
    return f"{code}.{exchange}"


def suffix_to_qlib(symbol: str) -> str:
    code, exchange = symbol.split(".", 1)
    return f"{exchange.lower()}{code}"


def qlib_to_suffix(symbol: str) -> str:
    value = str(symbol).lower()
    if value.startswith("sh"):
        return f"{value[2:]}.SH"
    if value.startswith("sz"):
        return f"{value[2:]}.SZ"
    return str(symbol).upper()


def load_symbols(args: argparse.Namespace) -> list[str]:
    raw: list[str] = []
    if args.symbols:
        raw.extend(args.symbols.replace(";", ",").replace("\n", ",").split(","))
    if args.symbols_file:
        path = Path(args.symbols_file)
        if not path.exists():
            raise FileNotFoundError(f"Symbols file not found: {path}")
        for line in path.read_text(encoding="utf-8-sig").splitlines():
            clean = line.split("#", 1)[0].strip()
            if clean:
                raw.extend(clean.replace(";", ",").split(","))
    symbols = []
    seen = set()
    for item in raw:
        symbol = normalize_symbol(item)
        if symbol and symbol not in seen:
            symbols.append(symbol)
            seen.add(symbol)
    if not symbols:
        raise ValueError("Please provide fixed stocks with --symbols or --symbols-file.")
    bj = [symbol for symbol in symbols if symbol.split(".", 1)[0].startswith(("8", "4", "92"))]
    if bj:
        raise ValueError(f"Beijing Exchange stocks are not supported: {', '.join(bj)}")
    return symbols


def load_stock_names() -> dict[str, str]:
    path = SHARED_ROOT / "parquet" / "stock_basic.parquet"
    if not path.exists():
        return {}
    frame = pd.read_parquet(path, columns=["code", "name"])
    frame = frame.dropna(subset=["code", "name"])
    return dict(zip(frame["code"].astype(str).str.upper(), frame["name"].astype(str)))


def load_status(signal_date: pd.Timestamp) -> pd.DataFrame:
    path = SHARED_ROOT / "parquet" / "stock_daily_status.parquet"
    if not path.exists():
        return pd.DataFrame()
    frame = pd.read_parquet(path)
    date_col = "date" if "date" in frame.columns else "trade_date"
    code_col = "code" if "code" in frame.columns else "symbol"
    frame[date_col] = pd.to_datetime(frame[date_col], errors="coerce").dt.normalize()
    frame[code_col] = frame[code_col].astype(str).str.upper()
    frame = frame[frame[date_col] == signal_date].copy()
    if frame.empty:
        return frame
    frame = frame.rename(columns={date_col: "signal_date", code_col: "symbol"})
    keep = ["symbol", "tradable", "is_st", "paused", "limit_status"]
    for col in keep:
        if col not in frame.columns:
            frame[col] = pd.NA
    return frame[keep].drop_duplicates("symbol", keep="last")


def load_execution_info() -> pd.DataFrame:
    path = SHARED_ROOT / "parquet" / "daily_bars.parquet"
    bars = pd.read_parquet(path, columns=["trade_date", "symbol", "open", "close", "amount", "paused"])
    bars["trade_date"] = pd.to_datetime(bars["trade_date"], errors="coerce").dt.normalize()
    bars["symbol"] = bars["symbol"].astype(str).str.upper()
    bars = bars.sort_values(["symbol", "trade_date"]).copy()
    bars["signal_date"] = bars.groupby("symbol")["trade_date"].shift(1)
    bars["prev_close"] = bars.groupby("symbol")["close"].shift(1)
    valid_prev = bars["prev_close"].notna() & (bars["prev_close"] > 0)
    bars["next_open_return"] = np.where(valid_prev, bars["open"] / bars["prev_close"] - 1.0, np.nan)
    threshold = bars["symbol"].map(limit_threshold)
    bars["next_open_limit_up"] = valid_prev & (bars["next_open_return"] >= threshold)
    return bars.rename(
        columns={
            "trade_date": "execution_date",
            "amount": "next_amount",
            "paused": "next_paused",
        }
    )[["signal_date", "symbol", "execution_date", "next_amount", "next_paused", "next_open_return", "next_open_limit_up"]]


def limit_threshold(symbol: object) -> float:
    code = str(symbol).upper().split(".", 1)[0]
    if code.startswith(("300", "301", "688", "689")):
        return 0.195
    return 0.095


def load_config(args: argparse.Namespace, signal_date: pd.Timestamp) -> dict[str, Any]:
    from qlib_factor_platform.config import load_yaml

    cfg = load_yaml("qlib_production.yaml")
    cfg["end_date"] = signal_date.strftime("%Y-%m-%d")
    cfg["test_start"] = args.test_start
    cfg["test_end"] = signal_date.strftime("%Y-%m-%d")
    cfg["train_start"] = args.train_start
    cfg["train_end"] = args.train_end
    cfg["valid_start"] = args.valid_start
    cfg["valid_end"] = args.valid_end
    cfg.setdefault("runtime", {})
    cfg["runtime"]["max_cpu_pct"] = 0.80
    cfg["runtime"]["thread_limit"] = args.threads
    cfg["label"] = {"name": "LABEL0", "expression": LABEL_EXPR}
    cfg["model"] = {
        "name": "lightgbm",
        "loss": "mse",
        "num_leaves": args.num_leaves,
        "learning_rate": args.learning_rate,
        "num_boost_round": args.num_boost_round,
        "early_stopping_rounds": args.early_stopping_rounds,
    }
    return cfg


def prepare_prediction_frame(pred: pd.DataFrame, labels: pd.DataFrame | None = None) -> pd.DataFrame:
    frame = pred.reset_index().rename(columns={"datetime": "signal_date", "instrument": "qlib_symbol"})
    frame["signal_date"] = pd.to_datetime(frame["signal_date"], errors="coerce").dt.normalize()
    frame["symbol"] = frame["qlib_symbol"].map(qlib_to_suffix)
    frame["pred_score"] = pd.to_numeric(frame["pred_score"], errors="coerce")
    if labels is not None and not labels.empty:
        label_frame = labels.reset_index().rename(columns={"datetime": "signal_date", "instrument": "qlib_symbol"})
        label_frame["signal_date"] = pd.to_datetime(label_frame["signal_date"], errors="coerce").dt.normalize()
        label_col = [col for col in label_frame.columns if col not in {"qlib_symbol", "signal_date"}][0]
        label_frame = label_frame.rename(columns={label_col: "actual_return"})
        frame = frame.merge(label_frame[["qlib_symbol", "signal_date", "actual_return"]], on=["qlib_symbol", "signal_date"], how="left")
        frame["actual_return"] = pd.to_numeric(frame["actual_return"], errors="coerce")
        frame["actual_up"] = np.where(frame["actual_return"].notna(), frame["actual_return"] > 0, pd.NA)
    return frame.dropna(subset=["pred_score"]).sort_values(["signal_date", "pred_score"], ascending=[True, False])


def load_labels(dataset, segment: str) -> pd.DataFrame:
    from qlib.data.dataset.handler import DataHandlerLP

    labels = dataset.prepare(segment, col_set="label", data_key=DataHandlerLP.DK_R)
    if isinstance(labels, pd.Series):
        labels = labels.to_frame("LABEL0")
    return labels


def build_calibration(valid: pd.DataFrame, bins: int) -> pd.DataFrame:
    usable = valid.dropna(subset=["pred_score", "actual_return"]).copy()
    if usable.empty:
        raise RuntimeError("Validation set has no usable labels for probability calibration.")
    usable["actual_up"] = usable["actual_return"] > 0
    unique_scores = usable["pred_score"].nunique()
    bin_count = max(2, min(int(bins), int(unique_scores), len(usable)))
    usable["bucket"] = pd.qcut(usable["pred_score"].rank(method="first"), q=bin_count, labels=False, duplicates="drop")
    calibration = (
        usable.groupby("bucket", dropna=True)
        .agg(
            min_score=("pred_score", "min"),
            max_score=("pred_score", "max"),
            avg_score=("pred_score", "mean"),
            sample_count=("actual_up", "size"),
            up_rate=("actual_up", "mean"),
            avg_return=("actual_return", "mean"),
        )
        .reset_index(drop=True)
        .sort_values("avg_score")
    )
    return calibration


def probability_from_calibration(scores: pd.Series, calibration: pd.DataFrame) -> pd.Series:
    centers = calibration["avg_score"].to_numpy(dtype=float)
    rates = calibration["up_rate"].to_numpy(dtype=float)
    if len(centers) == 1:
        return pd.Series(np.full(len(scores), rates[0]), index=scores.index)
    result = np.interp(scores.astype(float).to_numpy(), centers, rates, left=rates[0], right=rates[-1])
    return pd.Series(np.clip(result, 0.0, 1.0), index=scores.index)


def direction_text(prob: float) -> str:
    if prob >= 0.55:
        return "\u504f\u4e0a\u6da8"
    if prob <= 0.45:
        return "\u504f\u4e0b\u8dcc"
    return "\u9707\u8361"


def confidence_text(prob: float) -> str:
    distance = abs(prob - 0.5)
    if distance >= 0.15:
        return "\u9ad8"
    if distance >= 0.08:
        return "\u4e2d"
    return "\u4f4e"


def attach_probability(frame: pd.DataFrame, calibration: pd.DataFrame) -> pd.DataFrame:
    result = frame.copy()
    result["up_probability"] = probability_from_calibration(result["pred_score"], calibration)
    result["down_probability"] = 1.0 - result["up_probability"]
    result["pred_direction"] = result["up_probability"].map(direction_text)
    result["confidence"] = result["up_probability"].map(confidence_text)
    result["pred_up"] = result["up_probability"] >= 0.5
    if "actual_return" in result.columns:
        valid = result["actual_return"].notna()
        result["is_correct"] = np.where(valid, result["pred_up"] == (result["actual_return"] > 0), pd.NA)
    return result


def execution_filter(frame: pd.DataFrame, args: argparse.Namespace) -> pd.DataFrame:
    exec_info = load_execution_info()
    result = frame.merge(exec_info, on=["signal_date", "symbol"], how="left")
    result["executable"] = True
    result["block_reason"] = ""
    has_execution = result["execution_date"].notna()
    if args.min_next_amount > 0:
        low_amount = has_execution & (pd.to_numeric(result["next_amount"], errors="coerce").fillna(0.0) < args.min_next_amount)
        result.loc[low_amount, "executable"] = False
        result.loc[low_amount, "block_reason"] += "\u6b21\u65e5\u6210\u4ea4\u989d\u4e0d\u8db3;"
    if args.max_next_open_return is not None:
        high_open = has_execution & (pd.to_numeric(result["next_open_return"], errors="coerce") > float(args.max_next_open_return))
        result.loc[high_open, "executable"] = False
        result.loc[high_open, "block_reason"] += "\u6b21\u65e5\u5f00\u76d8\u6da8\u5e45\u8fc7\u9ad8;"
    paused = has_execution & result["next_paused"].astype("boolean").fillna(False).astype(bool)
    result.loc[paused, "executable"] = False
    result.loc[paused, "block_reason"] += "\u6b21\u65e5\u505c\u724c;"
    limit_up = has_execution & result["next_open_limit_up"].astype("boolean").fillna(False).astype(bool)
    result.loc[limit_up, "executable"] = False
    result.loc[limit_up, "block_reason"] += "\u6b21\u65e5\u5f00\u76d8\u6da8\u505c\u4e70\u4e0d\u5230;"
    unknown_execution = ~has_execution
    result.loc[unknown_execution, "block_reason"] = "\u7b49\u5f85\u6b21\u65e5\u5f00\u76d8\u786e\u8ba4;"
    result["block_reason"] = result["block_reason"].replace("", pd.NA)
    return result


def metrics(frame: pd.DataFrame, prefix: str) -> dict[str, Any]:
    labeled = frame.dropna(subset=["actual_return", "up_probability"]).copy()
    if labeled.empty:
        return {f"{prefix}_sample_count": 0}
    labeled["actual_up"] = labeled["actual_return"] > 0
    labeled["pred_up"] = labeled["up_probability"] >= 0.5
    high = labeled[(labeled["up_probability"] >= 0.60) | (labeled["up_probability"] <= 0.40)]
    up_pred = labeled[labeled["up_probability"] >= 0.55]
    down_pred = labeled[labeled["up_probability"] <= 0.45]
    by_month = labeled.assign(month=labeled["signal_date"].dt.to_period("M").astype(str))
    month_accuracy = by_month.groupby("month").apply(lambda g: float((g["pred_up"] == g["actual_up"]).mean()), include_groups=False)
    return {
        f"{prefix}_sample_count": int(len(labeled)),
        f"{prefix}_accuracy": float((labeled["pred_up"] == labeled["actual_up"]).mean()),
        f"{prefix}_avg_return": float(labeled["actual_return"].mean()),
        f"{prefix}_high_conf_sample_count": int(len(high)),
        f"{prefix}_high_conf_accuracy": float((high["pred_up"] == (high["actual_return"] > 0)).mean()) if not high.empty else np.nan,
        f"{prefix}_pred_up_count": int(len(up_pred)),
        f"{prefix}_pred_up_actual_up_rate": float((up_pred["actual_return"] > 0).mean()) if not up_pred.empty else np.nan,
        f"{prefix}_pred_up_avg_return": float(up_pred["actual_return"].mean()) if not up_pred.empty else np.nan,
        f"{prefix}_pred_down_count": int(len(down_pred)),
        f"{prefix}_pred_down_actual_up_rate": float((down_pred["actual_return"] > 0).mean()) if not down_pred.empty else np.nan,
        f"{prefix}_pred_down_avg_return": float(down_pred["actual_return"].mean()) if not down_pred.empty else np.nan,
        f"{prefix}_month_positive_accuracy_count": int((month_accuracy > 0.5).sum()),
        f"{prefix}_month_count": int(len(month_accuracy)),
    }


def build_report(summary: dict[str, Any], output_dir: Path) -> str:
    lines = [
        f"# {STRATEGY_NAME} 实验报告",
        "",
        f"- 实验编号：`{summary['experiment_id']}`",
        f"- 信号日：{summary['signal_date']}",
        f"- 固定股票数：{summary['fixed_symbol_count']}",
        f"- 标签：{LABEL_DESCRIPTION}，`{LABEL_EXPR}`",
        f"- 输出目录：`{output_dir}`",
        "",
        "## 关键指标",
        "",
        f"- 全市场测试样本：{summary.get('all_sample_count', 0)}",
        f"- 全市场方向准确率：{format_pct(summary.get('all_accuracy'))}",
        f"- 全市场高置信度准确率：{format_pct(summary.get('all_high_conf_accuracy'))}",
        f"- 固定股票池测试样本：{summary.get('fixed_sample_count', 0)}",
        f"- 固定股票池方向准确率：{format_pct(summary.get('fixed_accuracy'))}",
        f"- 固定股票池高置信度准确率：{format_pct(summary.get('fixed_high_conf_accuracy'))}",
        f"- 固定股票池预测上涨组平均收益：{format_pct(summary.get('fixed_pred_up_avg_return'))}",
        f"- 固定股票池预测下跌组平均收益：{format_pct(summary.get('fixed_pred_down_avg_return'))}",
        "",
        "## 使用结论",
        "",
        "第一版实验只用于验证模型是否有方向区分能力，不应直接作为自动交易指令。",
        "如果固定股票池高置信度准确率和预测上涨组平均收益不能稳定为正，该模型只能作为辅助参考。",
    ]
    return "\n".join(lines) + "\n"


def format_pct(value: Any) -> str:
    if value is None or pd.isna(value):
        return "--"
    return f"{float(value) * 100:.2f}%"


def main() -> None:
    parser = argparse.ArgumentParser(description="Run Qlib next-day direction experiment for fixed stocks.")
    parser.add_argument("--symbols", default="")
    parser.add_argument("--symbols-file", default="")
    parser.add_argument("--signal-date", default="auto")
    parser.add_argument("--train-start", default="2018-01-02")
    parser.add_argument("--train-end", default="2023-12-31")
    parser.add_argument("--valid-start", default="2024-01-01")
    parser.add_argument("--valid-end", default="2024-12-31")
    parser.add_argument("--test-start", default="2025-01-01")
    parser.add_argument("--threads", type=int, default=19)
    parser.add_argument("--num-leaves", type=int, default=210)
    parser.add_argument("--learning-rate", type=float, default=0.0421)
    parser.add_argument("--num-boost-round", type=int, default=200)
    parser.add_argument("--early-stopping-rounds", type=int, default=50)
    parser.add_argument("--calibration-bins", type=int, default=10)
    parser.add_argument("--min-next-amount", type=float, default=100_000_000.0)
    parser.add_argument("--max-next-open-return", type=float, default=0.03)
    parser.add_argument("--output-root", default=str(DEFAULT_OUTPUT_ROOT))
    parser.add_argument("--progress", default=str(DEFAULT_OUTPUT_ROOT / "progress.json"))
    args = parser.parse_args()

    symbols = load_symbols(args)
    latest_date = latest_shared_data_date()
    signal_date = latest_date if args.signal_date == "auto" else pd.Timestamp(args.signal_date).normalize()
    if latest_date < signal_date:
        raise RuntimeError(f"Shared data is not ready: target={signal_date.date()}, max={latest_date.date()}.")

    experiment_id = f"next_day_direction_{signal_date.strftime('%Y%m%d')}_{datetime.now().strftime('%H%M%S')}"
    output_dir = Path(args.output_root) / experiment_id
    progress_path = Path(args.progress)
    output_dir.mkdir(parents=True, exist_ok=True)

    write_progress(progress_path, status="running", step="init", experiment_id=experiment_id, signal_date=str(signal_date.date()))

    from qlib_factor_platform.jobs.batch_control import ensure_spawn_pythonpath
    from qlib_factor_platform.qlib_integration.workflow import _apply_runtime_thread_limits, _build_dataset, _build_model, _init_qlib

    cfg = load_config(args, signal_date)
    _apply_runtime_thread_limits(cfg)
    ensure_spawn_pythonpath(PROJECT_ROOT)
    write_json(output_dir / "effective_config.json", cfg)
    write_json(output_dir / "fixed_symbols.json", {"symbols": symbols})

    write_progress(progress_path, status="running", step="build_dataset", experiment_id=experiment_id)
    _init_qlib(cfg)
    dataset = _build_dataset(cfg)
    model = _build_model(cfg)

    write_progress(progress_path, status="running", step="fit", experiment_id=experiment_id)
    evals_result: dict[str, Any] = {}
    model.fit(dataset, evals_result=evals_result)

    write_progress(progress_path, status="running", step="predict", experiment_id=experiment_id)
    valid_pred = model.predict(dataset, segment="valid").to_frame("pred_score").sort_index()
    test_pred = model.predict(dataset, segment="test").to_frame("pred_score").sort_index()
    valid_labels = load_labels(dataset, "valid")
    test_labels = load_labels(dataset, "test")

    valid_frame = prepare_prediction_frame(valid_pred, valid_labels)
    calibration = build_calibration(valid_frame, args.calibration_bins)
    test_frame = attach_probability(prepare_prediction_frame(test_pred, test_labels), calibration)
    test_frame = execution_filter(test_frame, args)

    fixed = test_frame[test_frame["symbol"].isin(symbols)].copy()
    signal = test_frame[(test_frame["signal_date"] == signal_date) & (test_frame["symbol"].isin(symbols))].copy()
    names = load_stock_names()
    status = load_status(signal_date)
    if not signal.empty:
        signal["code"] = signal["symbol"].str.split(".", n=1).str[0]
        signal["name"] = signal["symbol"].map(names).fillna(signal["code"])
        if not status.empty:
            signal = signal.merge(status, on="symbol", how="left")
        signal["strategy_code"] = STRATEGY_CODE
        signal["strategy_name"] = STRATEGY_NAME
        signal["signal_date"] = signal["signal_date"].dt.strftime("%Y-%m-%d")
        signal = signal.sort_values("up_probability", ascending=False)

    write_progress(progress_path, status="running", step="export", experiment_id=experiment_id)
    calibration.to_csv(output_dir / "calibration.csv", index=False, encoding="utf-8-sig")
    test_frame.to_csv(output_dir / "all_test_predictions.csv", index=False, encoding="utf-8-sig")
    fixed.to_csv(output_dir / "fixed_pool_backtest_predictions.csv", index=False, encoding="utf-8-sig")
    signal.to_csv(output_dir / "tomorrow_predictions.csv", index=False, encoding="utf-8-sig")

    summary = {
        "experiment_id": experiment_id,
        "strategy_code": STRATEGY_CODE,
        "strategy_name": STRATEGY_NAME,
        "signal_date": signal_date.strftime("%Y-%m-%d"),
        "fixed_symbol_count": len(symbols),
        "output_dir": str(output_dir),
        "label_expression": LABEL_EXPR,
        "train": [args.train_start, args.train_end],
        "valid": [args.valid_start, args.valid_end],
        "test": [args.test_start, signal_date.strftime("%Y-%m-%d")],
        **metrics(test_frame, "all"),
        **metrics(fixed, "fixed"),
    }
    write_json(output_dir / "summary.json", summary)
    (output_dir / "report.md").write_text(build_report(summary, output_dir), encoding="utf-8")
    write_progress(progress_path, status="success", step="completed", **summary)

    print(json.dumps(summary, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
