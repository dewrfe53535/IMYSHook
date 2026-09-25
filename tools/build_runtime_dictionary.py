"""把维护用原文索引展开进远程词典，客户端无需分发 source.json。"""

from __future__ import annotations

import argparse
import json
from pathlib import Path


def build(translation: dict, source: dict) -> tuple[dict, int]:
    result = dict(translation)
    phrases = dict(translation.get("phrases", {}))
    groups = translation.get("groups", {})
    added = 0

    for group, entries in source.get("groups", {}).items():
        if not isinstance(entries, dict):
            continue
        translated_entries = groups.get(group, {})
        if not isinstance(translated_entries, dict):
            continue
        for key, original in entries.items():
            translated = translated_entries.get(key)
            if not isinstance(original, str) or not original:
                continue
            if not isinstance(translated, str) or not translated.strip():
                continue
            if original not in phrases:
                phrases[original] = translated
                added += 1

    result["phrases"] = phrases
    return result, added


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--translation", type=Path, required=True)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    args = parser.parse_args()

    translation = json.loads(args.translation.read_text(encoding="utf-8"))
    source = json.loads(args.source.read_text(encoding="utf-8"))
    result, added = build(translation, source)
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"{args.out}: added {added} source phrases, total {len(result['phrases'])}")


if __name__ == "__main__":
    main()
