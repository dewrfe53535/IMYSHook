"""
从游戏资源缓存里静态导出 **图里带字的 UI 贴图**，用于汉化改图。

数据来源（解密见 `imys_crypto.py`）：

* `atlas/commonlang.jp.encrypted` —— 语言相关图集（`SpriteAtlas` 名 = `CommonLang`），
  整包 67 张全部是随语言变化的 UI 图（「限界突破」「冥王スキル獲得」「イベント開催中」…）。
* `atlas/frame.jp.encrypted`      —— 语言相关框体图集，只挑名字里带 `Text`/`Label` 的。

产出：

* `img_src/<名>.png`              原图（**要修改的图**）
* `img_out/<lang>/<名>.png`       占位图（同尺寸、虚线框 + 图名 + 占位标记），替换成译文后直接生效

运行时的替换约定：把 `<名>.png` 放到 `BepInEx/plugins/img_out[/<lang>]/` 即可，
插件按 `Sprite.name` 匹配替换（见 `UiImages.cs`）。

用法：

    uv run --no-project --with UnityPy --with pycryptodome --with pillow python tools/extract_atlas_images.py
    uv run ... python tools/extract_atlas_images.py --game C:/.../imys_r_exe --lang zh_Hant
"""

from __future__ import annotations

import argparse
import io
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from imys_crypto import CACHE_REL, decrypt_bytes, find_game_root  # noqa: E402

#: 插件仓库根目录（tools/ 的上一级）——相对输出路径的基准
REPO_ROOT = Path(__file__).resolve().parents[1]

#: 整包导出的语言图集（值为缓存目录下的相对路径，脚本会自动补 `.jp.encrypted`）
FULL_BUNDLES = ["atlas/commonlang"]

#: 只挑文本类 sprite 的图集
TEXT_ONLY_BUNDLES = ["atlas/frame"]


def looks_like_text(name: str) -> bool:
    lowered = name.lower()
    return lowered.startswith("text") or "text" in lowered or "label" in lowered


def export_sprites(bundle_bytes: bytes, only_text: bool) -> dict[str, "object"]:
    """返回 {sprite 名: PIL.Image}。"""
    import UnityPy

    out: dict[str, object] = {}
    env = UnityPy.load(io.BytesIO(bundle_bytes))
    for obj in env.objects:
        if obj.type.name != "Sprite":
            continue
        data = obj.read()
        name = (getattr(data, "m_Name", "") or "").strip()
        if not name:
            continue
        if only_text and not looks_like_text(name):
            continue
        image = getattr(data, "image", None)
        if image is None:
            continue
        out[name] = image
    return out


def make_placeholder(image, name: str, font_path: Path | None):
    """同尺寸占位图：半透明底 + 虚线框 + 图名 + 占位标记。"""
    from PIL import Image, ImageDraw, ImageFont

    w, h = image.size
    canvas = Image.new("RGBA", (w, h), (24, 26, 34, 210))
    draw = ImageDraw.Draw(canvas)

    # 虚线边框
    step, dash = 18, 10
    for x in range(0, w, step):
        draw.line([(x, 0), (min(x + dash, w - 1), 0)], fill=(255, 196, 0, 255), width=max(2, w // 160))
        draw.line([(x, h - 1), (min(x + dash, w - 1), h - 1)], fill=(255, 196, 0, 255), width=max(2, w // 160))
    for y in range(0, h, step):
        draw.line([(0, y), (0, min(y + dash, h - 1))], fill=(255, 196, 0, 255), width=max(2, h // 160))
        draw.line([(w - 1, y), (w - 1, min(y + dash, h - 1))], fill=(255, 196, 0, 255), width=max(2, h // 160))

    def pick(size: int):
        if font_path and font_path.exists():
            try:
                return ImageFont.truetype(str(font_path), size)
            except OSError:
                pass
        return ImageFont.load_default()

    big = max(11, min(h // 3, w // 8, 44))
    small = max(9, big // 2)
    try:
        draw.text((w // 2, h // 2 - big // 2), "占位圖", font=pick(big), fill=(255, 196, 0, 255), anchor="mm")
        draw.text((w // 2, h // 2 + big), name, font=pick(small), fill=(226, 232, 240, 255), anchor="mm")
    except Exception:  # 极小图 anchor 可能不支持
        draw.text((2, 2), name, font=pick(small), fill=(226, 232, 240, 255))
    return canvas


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=None, help="游戏根目录（默认自动向上查找）")
    ap.add_argument("--src-out", default="img_src", help="原图输出目录")
    ap.add_argument("--out-dir", default="img_out", help="译文图根目录")
    ap.add_argument("--lang", default="zh_Hant", help="译文图语言子目录")
    ap.add_argument("--placeholder", action="store_true", default=True, help="生成占位图（默认开）")
    ap.add_argument("--no-placeholder", dest="placeholder", action="store_false")
    ap.add_argument("--bundle", action="append", help="只导出指定缓存包（相对 assetbundles 的路径，可重复；省略 .encrypted）")
    args = ap.parse_args()

    root = find_game_root(args.game)
    cache = root / CACHE_REL

    src_dir = Path(args.src_out)
    if not src_dir.is_absolute():
        src_dir = REPO_ROOT / src_dir
    out_dir = Path(args.out_dir)
    if not out_dir.is_absolute():
        out_dir = REPO_ROOT / out_dir
    out_dir = out_dir / args.lang

    font = Path(r"C:\Windows\Fonts\msjh.ttc")
    if not font.exists():
        font = Path(r"C:\Windows\Fonts\msyh.ttc")

    src_dir.mkdir(parents=True, exist_ok=True)
    out_dir.mkdir(parents=True, exist_ok=True)

    total = 0
    bundles = [(p, False) for p in args.bundle] if args.bundle else (
        [(p, False) for p in FULL_BUNDLES] + [(p, True) for p in TEXT_ONLY_BUNDLES]
    )
    for rel, only_text in bundles:
        suffix = "" if rel.endswith(".encrypted") else ".jp.encrypted"
        path = cache / f"{rel}{suffix}"
        if not path.exists():
            print(f"  - 跳过（未缓存）: {path}")
            continue
        sprites = export_sprites(decrypt_bytes(path.read_bytes()), only_text)
        print(f"  {rel}: {len(sprites)} 张{'(仅文本类)' if only_text else ''}")
        for name, image in sorted(sprites.items()):
            image.save(src_dir / f"{name}.png")
            if args.placeholder:
                make_placeholder(image, name, font).save(out_dir / f"{name}.png")
            total += 1

    print(f"\n原图   -> {src_dir}  ({total} 张)")
    if args.placeholder:
        print(f"占位图 -> {out_dir}  ({total} 张)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
