"""只读比较游戏 AssetBundleVersion 清单与本机加密包缓存。

用法：uv run --no-project python tools/audit_bundle_cache.py
不调用 irisProject 的批量下载函数（它会改 cwd/写进度文件），先精确列出缺包。
"""

from __future__ import annotations

import argparse
import struct
from pathlib import Path

from imys_crypto import CACHE_REL, find_game_root


def read_7bit_length(data: bytes, offset: int) -> tuple[int, int]:
    value = 0
    for shift in range(0, 35, 7):
        part = data[offset]
        offset += 1
        value |= (part & 0x7F) << shift
        if part < 128:
            return value, offset
    raise ValueError("invalid .NET length prefix")


def read_string(data: bytes, offset: int) -> tuple[str, int]:
    size, offset = read_7bit_length(data, offset)
    end = offset + size
    if end > len(data):
        raise ValueError("truncated AssetBundleVersion")
    return data[offset:end].decode("utf-8"), end


def read_versions(path: Path) -> dict[str, str]:
    data = path.read_bytes()
    count = struct.unpack_from("<I", data)[0]
    offset = 4
    result = {}
    for _ in range(count):
        name, offset = read_string(data, offset)
        version, offset = read_string(data, offset)
        result[name.lstrip("/")] = version
    if offset != len(data):
        raise ValueError(f"trailing bytes in AssetBundleVersion: {len(data) - offset}")
    return result


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=None)
    args = ap.parse_args()
    cache = find_game_root(args.game) / CACHE_REL
    entries = read_versions(cache / "AssetBundleVersion")
    missing = [name for name in entries if not (cache / (name + ".encrypted")).is_file()
               and not (cache / name).is_file()]
    print(f"manifest={len(entries)} cached={len(entries) - len(missing)} missing={len(missing)}")
    for name in missing:
        print(f"  {name}  version={entries[name]}")


if __name__ == "__main__":
    main()
