"""在解密的 UnityFS 与序列化对象中找精确原文（UTF-8 / UTF-16LE）。

用于确认提取器漏掉的文本是否仍存在于本机资源包，而非依赖运行时猜测。
"""

from __future__ import annotations

import argparse
import io
from pathlib import Path

from imys_crypto import CACHE_REL, decrypt_bytes, find_game_root


def main() -> None:
    import UnityPy

    ap = argparse.ArgumentParser()
    ap.add_argument("terms", nargs="+")
    ap.add_argument("--game", default=None)
    ap.add_argument("--scope", default="**/*.encrypted")
    args = ap.parse_args()
    cache = find_game_root(args.game) / CACHE_REL
    patterns = {term: (term.encode("utf-8"), term.encode("utf-16le")) for term in args.terms}
    hits = {term: [] for term in patterns}
    files = sorted(cache.glob(args.scope))
    for path in files:
        if not path.is_file():
            continue
        name = path.relative_to(cache).as_posix()
        try:
            data = decrypt_bytes(path.read_bytes())
            for term, (utf8, utf16) in patterns.items():
                if utf8 in data or utf16 in data:
                    hits[term].append(name + ":UnityFS")
            env = UnityPy.load(io.BytesIO(data))
            for obj in env.objects:
                if obj.type.name not in {"MonoBehaviour", "TextAsset", "GameObject"}:
                    continue
                raw = obj.get_raw_data()
                for term, (utf8, utf16) in patterns.items():
                    if utf8 in raw or utf16 in raw:
                        hits[term].append(f"{name}:{obj.type.name}#{obj.path_id}")
        except Exception:
            continue
    for term, sources in hits.items():
        print(f"{term}: {len(sources)} hits")
        for source in sources[:20]:
            print(f"  {source}")


if __name__ == "__main__":
    main()
