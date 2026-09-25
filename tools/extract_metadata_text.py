"""从 IL2CPP global-metadata.dat 的 string-literal 表精确提取日文文本。

这不是字节流启发式扫描。IL2CPP 元数据头给出了：

* stringLiteralOffset / stringLiteralCount：Il2CppStringLiteral 表（每项 8 字节）
* stringLiteralDataOffset / stringLiteralDataCount：所有字面量的 UTF-8 数据区

每项由 ``uint32 length`` 与 ``uint32 dataIndex`` 组成，因此可以无歧义地恢复
代码硬编码字符串，包括不在 prefab、TextAsset 和 AssetBundle 里的 UI 文案。
"""

from __future__ import annotations

import argparse
import json
import re
import struct
from pathlib import Path

from imys_crypto import find_game_root

REPO_ROOT = Path(__file__).resolve().parents[1]
METADATA_REL = Path("imys_r_Data/il2cpp_data/Metadata/global-metadata.dat")
SANITY = 0xFAB11BAF
ENTRY_SIZE = 8

_JAPANESE = re.compile(r"[\u3040-\u30ff\u3400-\u9fff\uf900-\ufaff]")
_CONTROL = re.compile(r"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]")
_TECHNICAL = re.compile(
    r"(^|[/\\])Assets[/\\]|https?://|\.(?:dll|cs|json|png|jpe?g|asset|prefab|shader)$",
    re.IGNORECASE,
)


def _is_candidate(text: str) -> bool:
    text = text.strip()
    if not (2 <= len(text) <= 2000):
        return False
    if not _JAPANESE.search(text) or _CONTROL.search(text):
        return False
    if _TECHNICAL.search(text):
        return False
    return True


def read_literals(path: Path) -> tuple[int, list[tuple[int, str]]]:
    blob = path.read_bytes()
    if len(blob) < 32:
        raise ValueError(f"元数据文件过小：{len(blob)} bytes")

    sanity, version, table_offset, table_size, data_offset, data_size = struct.unpack_from(
        "<6I", blob, 0
    )
    if sanity != SANITY:
        raise ValueError(f"不是 IL2CPP global-metadata.dat：sanity=0x{sanity:08X}")
    if table_size % ENTRY_SIZE:
        raise ValueError(f"string literal 表大小不是 8 的倍数：{table_size}")
    if table_offset + table_size > len(blob) or data_offset + data_size > len(blob):
        raise ValueError("string literal 表或数据区越界")

    found: list[tuple[int, str]] = []
    for index, entry_offset in enumerate(range(table_offset, table_offset + table_size, ENTRY_SIZE)):
        length, data_index = struct.unpack_from("<II", blob, entry_offset)
        if data_index + length > data_size:
            continue
        raw = blob[data_offset + data_index : data_offset + data_index + length]
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError:
            continue
        if _is_candidate(text):
            found.append((index, text))

    return version, found


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--game", default=None, help="游戏根目录；默认从当前目录向上查找")
    parser.add_argument("--out", default="i18n", help="输出目录")
    parser.add_argument("--lang", default="zh_Hant")
    args = parser.parse_args()

    game_root = find_game_root(args.game)
    metadata_path = game_root / METADATA_REL
    version, rows = read_literals(metadata_path)

    # 保留首次出现顺序用于审阅，JSON 里的 phrases 则按原文排序，便于稳定 diff。
    first_indices: dict[str, int] = {}
    for index, text in rows:
        first_indices.setdefault(text, index)

    out_dir = Path(args.out)
    if not out_dir.is_absolute():
        out_dir = REPO_ROOT / out_dir
    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / f"{args.lang}.metadata.source.json"
    payload = {
        "_meta": {
            "source": METADATA_REL.as_posix(),
            "metadataVersion": version,
            "literalCandidates": len(rows),
            "uniqueCandidates": len(first_indices),
            "firstLiteralIndices": {key: first_indices[key] for key in sorted(first_indices)},
        },
        "phrases": {key: "" for key in sorted(first_indices)},
    }
    out_path.write_text(json.dumps(payload, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")

    print(f"metadata version: {version}")
    print(f"日文 string literals: {len(rows)}（去重 {len(first_indices)}）")
    print(f"输出: {out_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
