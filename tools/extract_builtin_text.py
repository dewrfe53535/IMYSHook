"""
提取**内置**文本表（`imys_r_Data/resources.assets` 里的 JSON TextAsset）。

游戏文本有两大静态来源：

1. **下载缓存**里的 `assetbundles/textdefine/*.jp.encrypted`
   —— 加密包，由 `extract_textdefine.py` 处理。
2. **内置** `imys_r_Data/resources.assets` 里的 15 个 JSON TextAsset
   —— 未加密，与 1 同格式（`{"Key": "日文"}`），但**下载缓存里没有**。

第 2 类是之前漏掉的那一批（设置界面的 `SoundNotice`「このゲームでは音声が再生されます…」、
标题界面、账号绑定、追踪许可等都在这里）。

本脚本把第 2 类并入 `i18n/<lang>.source.json` 的 `groups`（组名 = TextAsset 名），
再由插件自动展开成「原文 → 译文」短语表。

用法：
    uv run --no-project --with UnityPy python tools/extract_builtin_text.py
    uv run ... python tools/extract_builtin_text.py --dump      # 只打印，不写文件
"""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from imys_crypto import find_game_root  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[1]

#: 内置资源文件（相对游戏根目录）
BUILTIN_FILES = ["imys_r_Data/resources.assets"]


def load_builtin(groups: dict[str, dict[str, str]], root: Path) -> None:
    import UnityPy

    for rel in BUILTIN_FILES:
        path = root / rel
        if not path.exists():
            print(f"  - 跳过（不存在）: {rel}")
            continue
        env = UnityPy.load(str(path))
        n = 0
        for obj in env.objects:
            if obj.type.name != "TextAsset":
                continue
            data = obj.read()
            raw = getattr(data, "m_Script", None) or getattr(data, "script", None)
            if isinstance(raw, bytes):
                raw = raw.decode("utf-8-sig", "ignore")
            raw = (raw or "").lstrip("\ufeff").strip()
            if not raw.startswith("{"):
                continue
            try:
                parsed = json.loads(raw)
            except json.JSONDecodeError:
                continue
            if not isinstance(parsed, dict):
                continue
            name = (getattr(data, "m_Name", "") or "unnamed").strip()
            entries = {k: v for k, v in parsed.items() if isinstance(v, str)}
            if not entries:
                continue
            groups.setdefault(name, {}).update(entries)
            n += len(entries)
            print(f"  {name:32s} {len(entries):4d}")
        print(f"  {rel}: 共 {n} 条")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=None)
    ap.add_argument("--out", default="i18n")
    ap.add_argument("--lang", default="zh_Hant")
    ap.add_argument("--dump", action="store_true", help="只打印，不写文件")
    args = ap.parse_args()

    root = find_game_root(args.game)
    groups: dict[str, dict[str, str]] = {}
    print("扫描内置文本表:")
    load_builtin(groups, root)

    total = sum(len(v) for v in groups.values())
    print(f"\n内置文本表合计 {len(groups)} 组 / {total} 条")

    if args.dump:
        for g, kv in sorted(groups.items()):
            print(f"### {g}")
            for k, v in kv.items():
                print(k + "\t" + v.replace("\n", "\\n"))
        return 0

    out_dir = Path(args.out)
    if not out_dir.is_absolute():
        out_dir = REPO_ROOT / out_dir
    src_path = out_dir / f"{args.lang}.source.json"
    if not src_path.exists():
        print(f"缺少 {src_path}，请先跑 extract_textdefine.py", file=sys.stderr)
        return 1

    data = json.loads(src_path.read_text(encoding="utf-8"))
    dst = data.setdefault("groups", {})
    added = 0
    for g, kv in groups.items():
        bucket = dst.setdefault(g, {})
        for k, v in kv.items():
            if k not in bucket:
                added += 1
            bucket[k] = v
    src_path.write_text(json.dumps(data, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"并入 {added} 条新 key 到 {src_path}（现有 group 数 {len(dst)}）")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
