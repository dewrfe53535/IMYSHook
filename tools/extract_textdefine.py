"""
从游戏资源缓存里静态提取 **全部 UI 文本表**，生成汉化词典骨架。

数据来源：`imys_r_Data/Caches/assetbundles/textdefine/*.jp.encrypted`
（游戏内对应 `DMM.OLG.Unity.Engine.Localize` / `Hachiroku.TextManager` 的取词表）
解密后是 Unity AssetBundle，内含一个 JSON TextAsset：`{"Key": "日文原文"}`。

输出（默认写到 `BepInEx/plugins/i18n/`，仓库内即 `IMYSHook/i18n/`）：

* `<lang>.source.json` —— 只读参考：`{"groups": {组名: {key: 日文原文}}}`
* `<lang>.json`        —— 若不存在则生成空值骨架（key 全在、value 为空，等待填译文）；
                          若已存在则**不覆盖**，只提示缺失的 key。

用法：

    uv run --no-project --with UnityPy --with pycryptodome python tools/extract_textdefine.py
    uv run ... python tools/extract_textdefine.py --game C:/path/to/imys_r_exe --out i18n --lang zh_Hant
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from imys_crypto import CACHE_REL, decrypt_bytes, find_game_root  # noqa: E402

#: 插件仓库根目录（tools/ 的上一级）——相对输出路径的基准
REPO_ROOT = Path(__file__).resolve().parents[1]


def load_textassets(bundle_bytes: bytes) -> dict[str, str]:
    """从解密后的 bundle 里读出所有 JSON TextAsset 并合并成 {key: 原文}。"""
    import io

    import UnityPy

    merged: dict[str, str] = {}
    env = UnityPy.load(io.BytesIO(bundle_bytes))
    for obj in env.objects:
        if obj.type.name != "TextAsset":
            continue
        data = obj.read()
        raw = getattr(data, "m_Script", None) or getattr(data, "script", None)
        if isinstance(raw, bytes):
            raw = raw.decode("utf-8-sig")
        raw = raw.lstrip("\ufeff")
        if not raw.strip():
            continue
        try:
            parsed = json.loads(raw)
        except json.JSONDecodeError as exc:
            print(f"  ! JSON 解析失败: {exc}", file=sys.stderr)
            continue
        for key, value in parsed.items():
            if isinstance(value, str):
                merged[key] = value
    return merged


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=None, help="游戏根目录（默认自动向上查找）")
    ap.add_argument("--out", default="i18n", help="词典输出目录（相对游戏根目录或绝对路径）")
    ap.add_argument("--lang", default="zh_Hant", help="目标语言标签")
    args = ap.parse_args()

    root = find_game_root(args.game)
    src_dir = root / CACHE_REL / "textdefine"
    files = sorted(src_dir.glob("*.encrypted"))
    if not files:
        print(f"没有找到文本表: {src_dir}", file=sys.stderr)
        return 1

    out_dir = Path(args.out)
    if not out_dir.is_absolute():
        out_dir = REPO_ROOT / out_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    groups: dict[str, dict[str, str]] = {}
    for path in files:
        group = path.name.replace(".encrypted", "").replace(".jp", "")
        try:
            entries = load_textassets(decrypt_bytes(path.read_bytes()))
        except Exception as exc:  # noqa: BLE001
            print(f"  ! {path.name}: {exc}", file=sys.stderr)
            continue
        if entries:
            groups.setdefault(group, {}).update(entries)
        print(f"  {group:28s} {len(entries):4d}")

    total = sum(len(v) for v in groups.values())
    print(f"\n合计 {len(groups)} 组 / {total} 条")

    source_path = out_dir / f"{args.lang}.source.json"
    source_path.write_text(
        json.dumps({"groups": groups}, ensure_ascii=False, indent=1) + "\n", encoding="utf-8"
    )
    print(f"参考原文  -> {source_path}")

    trans_path = out_dir / f"{args.lang}.json"
    if trans_path.exists():
        existing = json.loads(trans_path.read_text(encoding="utf-8"))
        known = {k for bucket in (existing.get("groups") or {}).values() for k in bucket}
        known |= set((existing.get("keys") or {}).keys())
        missing = total - sum(1 for g in groups.values() for k in g if k in known)
        print(f"翻译文件已存在，未覆盖 -> {trans_path}（还缺 {missing} 条）")
    else:
        skeleton = {
            "_meta": {
                "note": "value 留空表示未翻译；原文见同名 .source.json。"
                        "插件只加载 <lang>.json / <lang>.local.json，本骨架可改名使用。",
            },
            "groups": {g: {k: "" for k in entries} for g, entries in groups.items()},
        }
        trans_path.write_text(
            json.dumps(skeleton, ensure_ascii=False, indent=1) + "\n", encoding="utf-8"
        )
        print(f"翻译骨架  -> {trans_path}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
