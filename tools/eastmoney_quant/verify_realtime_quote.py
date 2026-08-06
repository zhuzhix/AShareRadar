import json
import os
from datetime import datetime
from pathlib import Path

from gm.api import current, set_token


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


def main():
    token = os.environ.get("EASTMONEY_QUANT_TOKEN", "").strip()
    if not token:
        raise RuntimeError("EASTMONEY_QUANT_TOKEN is required.")

    set_token(token)
    symbols = "SHSE.000001,SZSE.000001,SZSE.300059"
    quotes = current(symbols=symbols)
    payload = {
        "checked_at": datetime.now().isoformat(timespec="seconds"),
        "symbols": symbols.split(","),
        "count": len(quotes) if quotes is not None else 0,
        "quotes": [normalize_item(item) for item in (quotes or [])],
    }

    output_path = Path(__file__).with_name("verify_realtime_quote_result.json")
    output_path.write_text(json.dumps(payload, ensure_ascii=False, indent=2, default=str), encoding="utf-8")
    print(json.dumps({
        "checked_at": payload["checked_at"],
        "count": payload["count"],
        "symbols": payload["symbols"],
    }, ensure_ascii=False))


if __name__ == "__main__":
    main()
