"""合并 textdefine、预制体/场景与 IL2CPP metadata 的日文原文索引。

输出维护用 `i18n/<lang>.ui-source-index.json`，保留每句的来源；客户端不加载它。
"""

from __future__ import annotations

import argparse
import json
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def build_index(source_dir: Path, lang: str) -> dict:
    base = source_dir / lang
    keyed = json.loads(base.with_suffix(".source.json").read_text(encoding="utf-8"))
    prefab = json.loads(base.with_suffix(".prefab.source.json").read_text(encoding="utf-8"))
    metadata = json.loads(base.with_suffix(".metadata.source.json").read_text(encoding="utf-8"))

    sources: dict[str, set[str]] = defaultdict(set)
    for group, entries in keyed["groups"].items():
        for key, original in entries.items():
            if original:
                sources[original].add(f"textdefine:{group}/{key}")
    for original, location in prefab["_meta"]["sources"].items():
        sources[original].add(f"serialized:{location}")
    for original, index in metadata["_meta"]["firstLiteralIndices"].items():
        sources[original].add(f"metadata:literal#{index}")

    return {
        "_meta": {
            "note": "静态原文索引；不表示已翻译。source 值保留资源/键/字面量位置。",
            "uniqueOriginals": len(sources),
            "textdefineEntries": sum(map(len, keyed["groups"].values())),
            "serializedOriginals": len(prefab["phrases"]),
            "metadataOriginals": len(metadata["phrases"]),
        },
        "originals": {original: sorted(locations) for original, locations in sorted(sources.items())},
    }


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--lang", default="zh_Hant")
    ap.add_argument("--source-dir", default=str(ROOT / "i18n"))
    ap.add_argument("--out", default=None)
    args = ap.parse_args()

    source_dir = Path(args.source_dir)
    result = build_index(source_dir, args.lang)
    out = Path(args.out) if args.out else source_dir / f"{args.lang}.ui-source-index.json"
    out.write_text(json.dumps(result, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"{len(sources)} unique originals -> {out}")


if __name__ == "__main__":
    main()
