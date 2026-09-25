"""
从 **prefab / scene 资源**里静态提取「烤进界面的日文文本」（第二批来源）。

游戏界面文本的静态来源共三类：

1. 下载缓存 `assetbundles/textdefine/*.jp.encrypted`   → `extract_textdefine.py`（key → 文本表）
2. 内置 `imys_r_Data/resources.assets` 里的 JSON TextAsset → `extract_builtin_text.py`（key → 文本表）
3. **prefab / scene 里直接烤在组件上的文本**             → 本脚本（无 key，只能按「原文」翻译）

第 3 类的序列化形式是 `[uint32 字节长度][UTF-8 字节]`（例：设置界面的
`ゲーム内のすべての音をミュートする`、`タイトル画面でOPを再生する`、`ゲームスタート`、`ボイス`）。
MonoBehaviour 没有 typetree、UnityPy 读不了字段，但长度前缀足够精确地切出字符串。

输出 `i18n/<lang>.prefab.source.json` = `{"phrases": {"<原文>": ""}}`，
翻译后并入 `<lang>.json` 的 `phrases` 即可（插件会用它兜住一切不经 key 的路径）。

用法：
    uv run --no-project --with pycryptodome python tools/extract_prefab_text.py
    uv run ... python tools/extract_prefab_text.py --scope-builtin --scope-bundle "common/prefabs/*.encrypted"
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from imys_crypto import CACHE_REL, decrypt_bytes, find_game_root  # noqa: E402

REPO_ROOT = Path(__file__).resolve().parents[1]

#: 内置资源（相对游戏根目录）
BUILTIN_FILES = [
    # 代码里的硬编码字符串字面量也住在这里（例：`ゲームを終了します`）
    "imys_r_Data/il2cpp_data/Metadata/global-metadata.dat",
    "imys_r_Data/resources.assets",
    "imys_r_Data/globalgamemanagers.assets",
    "imys_r_Data/sharedassets0.assets",
    "imys_r_Data/sharedassets1.assets",
    "imys_r_Data/sharedassets2.assets",
    "imys_r_Data/sharedassets3.assets",
]

DEFAULT_BUNDLE_SCOPES = [
    "**/*.encrypted",
]

_ALLOWED = re.compile(
    "[\t\r\n\u0020-\u007e"
    "\u3000-\u303f"
    "\u3040-\u309f"
    "\u30a0-\u30ff"
    "\u4e00-\u9fff"
    "\uf900-\ufaff"
    "\uff01-\uff60\uffe0-\uffe6"
    "\u00a5\u00b0\u00d7\u2026\u203b\u2190-\u2193\u25a0-\u25cf\u2605\u2606\u266a\u2600-\u26ff"
    "]+$"
)
_BAD = re.compile(
    r"[\uac00-\ud7af\u1100-\u11ff\u3130-\u318f\u3400-\u4dbf\ufffd]|"
    r"(https?://|/assets/|\.(dll|png|jpg|json|txt|cs|mp4|acb|awb)$)|^[\x20-\x7e]{0,3}$"
)
_PREFIX = re.compile(rb"(?=[\x02-\xff]\x00\x00\x00|[\x00-\xff][\x01-\x1f]\x00\x00)")


def _sane(s: str) -> bool:
    if not (2 <= len(s) <= 4096):
        return False
    if s.lstrip().startswith(("{", "[")):
        try:
            if isinstance(json.loads(s), (dict, list)):
                return False  # TextAsset 容器需按键值解析，不能把整段 JSON 当成一句 UI 文案
        except json.JSONDecodeError:
            pass
    if _BAD.search(s):
        return False
    if not _ALLOWED.fullmatch(s):
        return False
    return bool(re.search(r"[\u3040-\u30ff\u4e00-\u9fff]", s))


def iter_prefixed_strings(blob: bytes):
    """按 `[uint32 字节长度][UTF-8]` 精确切串。"""
    import struct

    size = len(blob)
    # 资源包总量以 GB 计；逐字节 Python 循环非常慢。先在 C 实现的正则里
    # 找到高两字节为零的合法长度前缀，再校验 UTF-8 和文本内容。
    for match in _PREFIX.finditer(blob):
        i = match.start()
        (length,) = struct.unpack_from("<I", blob, i)
        if 2 <= length <= 8191 and i + 4 + length <= size:
            raw = blob[i + 4 : i + 4 + length]
            try:
                s = raw.decode("utf-8")
            except UnicodeDecodeError:
                continue
            if _sane(s):
                yield s


def collect(root: Path, bundle_scopes: list[str], use_builtin: bool) -> dict[str, str]:
    import io

    import UnityPy

    found: dict[str, str] = {}

    def eat(blob: bytes, label: str) -> None:
        n = 0
        for s in iter_prefixed_strings(blob):
            if s not in found:
                found[s] = label
                n += 1
        if n and ":MonoBehaviour#" not in label and ":TextAsset#" not in label and ":GameObject#" not in label:
            print(f"  {label:58s} +{n}")

    def eat_objects(env, label: str) -> None:
        before = len(found)
        for obj in env.objects:
            if obj.type.name not in {"MonoBehaviour", "TextAsset", "GameObject"}:
                continue
            try:
                eat(obj.get_raw_data(), f"{label}:{obj.type.name}#{obj.path_id}")
            except Exception as exc:  # noqa: BLE001
                print(f"  ! {label}:{obj.path_id}: {exc}", file=sys.stderr)
        added = len(found) - before
        if added:
            print(f"  {label + ' (serialized objects)':58s} +{added}")

    if use_builtin:
        for rel in BUILTIN_FILES:
            path = root / rel
            if path.exists():
                eat(path.read_bytes(), rel)
                if path.suffix == ".assets":
                    try:
                        eat_objects(UnityPy.load(str(path)), rel)
                    except Exception as exc:  # noqa: BLE001
                        print(f"  ! {rel}: {exc}", file=sys.stderr)

    cache = root / CACHE_REL
    for scope in bundle_scopes:
        for path in sorted(cache.glob(scope)):
            if not path.is_file():
                continue
            try:
                label = path.relative_to(cache).as_posix()
                bundle = decrypt_bytes(path.read_bytes())
                eat(bundle, label)
                eat_objects(UnityPy.load(io.BytesIO(bundle)), label)
            except Exception as exc:  # noqa: BLE001
                print(f"  ! {path.name}: {exc}", file=sys.stderr)

    return found


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=None)
    ap.add_argument("--out", default="i18n")
    ap.add_argument("--lang", default="zh_Hant")
    ap.add_argument("--scope-bundle", nargs="*", default=DEFAULT_BUNDLE_SCOPES)
    ap.add_argument("--no-builtin", dest="builtin", action="store_false")
    ap.add_argument("--append", action="store_true", help="把未收录的原文以空值并入 <lang>.json 的 phrases")
    args = ap.parse_args()

    root = find_game_root(args.game)
    print("扫描内置资源 + 资源包 prefab/scene …")
    found = collect(root, args.scope_bundle, args.builtin)

    out_dir = Path(args.out)
    if not out_dir.is_absolute():
        out_dir = REPO_ROOT / out_dir
    out_dir.mkdir(parents=True, exist_ok=True)

    src_path = out_dir / f"{args.lang}.prefab.source.json"
    if src_path.exists():
        old = json.loads(src_path.read_text(encoding="utf-8"))
        for original in old.get("phrases", {}):
            found.setdefault(original, "previous-source")
    print(f"\n合计候选原文 {len(found)} 条")
    src_path.write_text(
        json.dumps({
            "_meta": {"sources": {k: found[k] for k in sorted(found)}},
            "phrases": {k: "" for k in sorted(found)},
        }, ensure_ascii=False, indent=1) + "\n",
        encoding="utf-8",
    )
    print(f"原文模板 -> {src_path}")

    if args.append:
        main_path = out_dir / f"{args.lang}.json"
        data = json.loads(main_path.read_text(encoding="utf-8"))
        phrases = data.setdefault("phrases", {})
        added = sum(1 for k in found if k not in phrases)
        for k in found:
            phrases.setdefault(k, "")
        main_path.write_text(json.dumps(data, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
        print(f"并入 {added} 条空值到 {main_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
