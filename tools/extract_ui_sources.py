"""一次提取游戏的静态 UI 原文，并生成可供翻译协作的差异报告。

只写 ``*.source.json``、``*.ui-source-index.json`` 和提取报告；绝不改译文词典。
依赖：UnityPy、pycryptodome（建议用 uv run --no-project --with 安装）。
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

from audit_bundle_cache import read_versions
from build_ui_source_index import build_index
from extract_builtin_text import load_builtin
from extract_metadata_text import METADATA_REL, read_literals
from extract_prefab_text import DEFAULT_BUNDLE_SCOPES, collect
from extract_textdefine import load_textassets
from imys_crypto import CACHE_REL, decrypt_bytes, find_game_root

REPO_ROOT = Path(__file__).resolve().parents[1]


def read_json(path: Path) -> dict:
    if not path.is_file():
        return {}
    return json.loads(path.read_text(encoding="utf-8"))


def write_json(path: Path, payload: dict) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")


def keyed_diff(old: dict, new: dict) -> dict:
    """对分组 key→原文作稳定差分，区分新增、删除和原文变动。"""
    before = {(g, k): v for g, entries in old.items() for k, v in entries.items()}
    after = {(g, k): v for g, entries in new.items() for k, v in entries.items()}
    return {
        "added": [{"group": g, "key": k, "original": after[g, k]}
                  for g, k in sorted(after.keys() - before.keys())],
        "removed": [{"group": g, "key": k, "original": before[g, k]}
                    for g, k in sorted(before.keys() - after.keys())],
        "changed": [{"group": g, "key": k, "before": before[g, k], "after": after[g, k]}
                    for g, k in sorted(before.keys() & after.keys()) if before[g, k] != after[g, k]],
    }


def phrase_diff(old: dict, new: dict) -> dict:
    return {"added": sorted(new.keys() - old.keys()), "removed": sorted(old.keys() - new.keys())}


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__)
    ap.add_argument("--game", default=None, help="游戏根目录；默认从当前目录向上查找")
    ap.add_argument("--out", default="i18n", help="输出目录；相对路径以项目根目录为基准")
    ap.add_argument("--lang", default="zh_Hant")
    ap.add_argument("--scope-bundle", action="append", default=None,
                    help="资源包 glob；可重复，默认扫描全部 *.encrypted")
    ap.add_argument("--no-builtin-serialized", action="store_true",
                    help="序列化文本扫描不读内置文件（内置 JSON 文本表仍提取）")
    ap.add_argument("--strict-cache", action="store_true", help="资源包缓存不全则停止，不写输出")
    args = ap.parse_args()

    root = find_game_root(args.game)
    out = Path(args.out)
    if not out.is_absolute():
        out = REPO_ROOT / out
    out.mkdir(parents=True, exist_ok=True)
    cache = root / CACHE_REL
    manifest_path = cache / "AssetBundleVersion"
    versions = read_versions(manifest_path)
    missing = sorted(name for name in versions
                     if not (cache / (name + ".encrypted")).is_file()
                     and not (cache / name).is_file())
    print(f"缓存清单 {len(versions)} 项，缺失 {len(missing)} 项")
    if missing:
        print("警告：缺包会使本次索引不完整；详细路径见提取报告。", file=sys.stderr)
        if args.strict_cache:
            return 2

    prefix = out / args.lang
    source_path = prefix.with_suffix(".source.json")
    prefab_path = prefix.with_suffix(".prefab.source.json")
    metadata_path = prefix.with_suffix(".metadata.source.json")
    index_path = prefix.with_suffix(".ui-source-index.json")
    old_keyed = read_json(source_path).get("groups", {})
    old_prefab = read_json(prefab_path).get("phrases", {})
    old_metadata = read_json(metadata_path).get("phrases", {})
    old_index = read_json(index_path).get("originals", {})

    groups: dict[str, dict[str, str]] = {}
    textdefine_errors: list[str] = []
    textdefine_paths = sorted((cache / "textdefine").glob("*.jp.encrypted"))
    if not textdefine_paths:
        raise FileNotFoundError(f"找不到 textdefine 资源：{cache / 'textdefine'}")
    print(f"提取 textdefine：{len(textdefine_paths)} 个资源包")
    for path in textdefine_paths:
        group = path.name.removesuffix(".encrypted").removesuffix(".jp")
        try:
            entries = load_textassets(decrypt_bytes(path.read_bytes()))
            if entries:
                groups.setdefault(group, {}).update(entries)
        except Exception as exc:  # 单包错误不能悄悄当作该组被删除
            textdefine_errors.append(f"{path.name}: {exc}")
    if textdefine_errors:
        raise RuntimeError("textdefine 提取失败，未改写旧索引：\n" + "\n".join(textdefine_errors))
    print("提取内置 JSON TextAsset")
    load_builtin(groups, root)
    write_json(source_path, {"groups": groups})

    print("提取序列化 UI 文本（全缓存扫描，可能需要几分钟）")
    serialized = collect(root, args.scope_bundle or DEFAULT_BUNDLE_SCOPES,
                         not args.no_builtin_serialized)
    write_json(prefab_path, {
        "_meta": {"sources": {s: serialized[s] for s in sorted(serialized)}},
        "phrases": {s: "" for s in sorted(serialized)},
    })

    print("提取 IL2CPP metadata 字面量")
    metadata_file = root / METADATA_REL
    metadata_version, literal_rows = read_literals(metadata_file)
    first_indices: dict[str, int] = {}
    for index, original in literal_rows:
        first_indices.setdefault(original, index)
    write_json(metadata_path, {
        "_meta": {
            "source": METADATA_REL.as_posix(),
            "metadataVersion": metadata_version,
            "literalCandidates": len(literal_rows),
            "uniqueCandidates": len(first_indices),
            "firstLiteralIndices": {s: first_indices[s] for s in sorted(first_indices)},
        },
        "phrases": {s: "" for s in sorted(first_indices)},
    })

    index = build_index(out, args.lang)
    write_json(index_path, index)
    originals = index["originals"]
    report = {
        "_meta": {
            "generatedAtUtc": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            "gameRoot": str(root),
            "note": "与本输出目录的上一次提取比较；首次运行所有条目均计为新增。",
            "translationFilesModified": False,
        },
        "input": {
            "assetBundleManifestSha256": hashlib.sha256(manifest_path.read_bytes()).hexdigest(),
            "metadataSha256": hashlib.sha256(metadata_file.read_bytes()).hexdigest(),
            "manifestEntries": len(versions),
            "missingBundles": missing,
            "textdefineBundles": len(textdefine_paths),
        },
        "counts": index["_meta"],
        "changes": {
            "textdefineAndBuiltin": keyed_diff(old_keyed, groups),
            "serialized": phrase_diff(old_prefab, {s: "" for s in serialized}),
            "metadata": phrase_diff(old_metadata, {s: "" for s in first_indices}),
            "uniqueOriginals": phrase_diff(old_index, originals),
        },
    }
    report_path = out / f"{args.lang}.extraction-report.json"
    write_json(report_path, report)
    counts = index["_meta"]
    print(f"完成：{counts['textdefineEntries']} 个表项、{counts['serializedOriginals']} 个序列化原文、"
          f"{counts['metadataOriginals']} 个 metadata 原文；去重后 {counts['uniqueOriginals']} 条")
    print(f"来源索引：{index_path}")
    print(f"差异报告：{report_path}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
