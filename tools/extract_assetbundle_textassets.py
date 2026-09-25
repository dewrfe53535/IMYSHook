"""结构化扫描所有加密 AssetBundle 内的 JSON TextAsset，保留源 bundle 与对象名。

和 prefab 的长度前缀扫描互补：这里提取 key → 原文，不把整个 JSON 容器误当一句短语。
用法：uv run --no-project --with UnityPy --with pycryptodome python tools/extract_assetbundle_textassets.py
"""

from __future__ import annotations

import argparse
import io
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from imys_crypto import CACHE_REL, decrypt_bytes, find_game_root  # noqa: E402


def main() -> int:
    import UnityPy

    parser = argparse.ArgumentParser()
    parser.add_argument("--game", default=None)
    parser.add_argument("--out", default="i18n/assetbundle_textassets.source.json")
    parser.add_argument("--scope", default="**/*.encrypted")
    args = parser.parse_args()

    root = find_game_root(args.game)
    cache = root / CACHE_REL
    groups: dict[str, dict[str, str]] = {}
    provenance: dict[str, str] = {}
    files = sorted(cache.glob(args.scope))
    for index, path in enumerate(files, 1):
        if not path.is_file():
            continue
        try:
            env = UnityPy.load(io.BytesIO(decrypt_bytes(path.read_bytes())))
            for obj in env.objects:
                if obj.type.name != "TextAsset":
                    continue
                data = obj.read()
                raw = getattr(data, "m_Script", None) or getattr(data, "script", None)
                if isinstance(raw, bytes):
                    raw = raw.decode("utf-8-sig", "ignore")
                if not isinstance(raw, str) or not raw.lstrip().startswith("{"):
                    continue
                try:
                    parsed = json.loads(raw)
                except json.JSONDecodeError:
                    continue
                if not isinstance(parsed, dict):
                    continue
                entries = {k: v for k, v in parsed.items() if isinstance(v, str)}
                if not entries or not any(any("\u3040" <= c <= "\u9fff" for c in v) for v in entries.values()):
                    continue
                name = (getattr(data, "m_Name", "") or "unnamed").strip()
                group = path.relative_to(cache).as_posix() + ":" + name
                groups[group] = entries
                provenance[group] = f"{len(entries)} entries"
                print(f"  {group} {len(entries)}", flush=True)
        except Exception as exc:  # noqa: BLE001
            print(f"  ! {path.relative_to(cache)}: {exc}", file=sys.stderr, flush=True)
        if index % 500 == 0:
            print(f"scanned {index}/{len(files)} bundles", flush=True)

    out = Path(args.out)
    if not out.is_absolute():
        out = Path(__file__).resolve().parents[1] / out
    out.parent.mkdir(parents=True, exist_ok=True)
    out.write_text(json.dumps({"groups": groups}, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"{len(groups)} TextAssets / {sum(map(len, groups.values()))} entries -> {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
