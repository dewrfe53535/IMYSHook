"""导出幻灯片式帮助的原文，格式与剧情翻译接口相同：{原文: 译文}。

不包含 mHelpDetails 指向的 html/howto 帮助，也不处理图片上的烘焙文字。
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path


TABLES = {
    "mHelpAccesses": "imys_help_accesses",
    "mEventHelpAccesses": "imys_event_help_accesses",
    "mEventPermanentHelpAccesses": "imys_event_permanent_help_accesses",
}
FIELDS = ("title", "description")


def extract(table_path: Path) -> dict[str, str]:
    data = json.loads(table_path.read_text(encoding="utf-8"))
    if data.get("name") != table_path.stem or not isinstance(data.get("data"), list):
        raise ValueError(f"不是预期的帮助数据表: {table_path}")
    originals = {
        row[field]: ""
        for row in data["data"]
        for field in FIELDS
        if isinstance(row.get(field), str) and row[field].strip()
    }
    return dict(sorted(originals.items()))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--masterdata", type=Path, required=True, help="含帮助数据表 JSON 的目录")
    parser.add_argument("--out", type=Path, required=True, help="输出目录（每张表一份平铺 JSON）")
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=True)
    for table, label in TABLES.items():
        source = args.masterdata / f"{table}.json"
        if not source.is_file():
            raise FileNotFoundError(source)
        originals = extract(source)
        destination = args.out / f"{label}.json"
        destination.write_text(json.dumps(originals, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        print(f"{label}: {len(originals)} 条 -> {destination}")


if __name__ == "__main__":
    main()
