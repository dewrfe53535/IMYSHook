"""为词典、图片与字体生成带 SHA-256 的远程清单（`*.version.json` / `manifest.json`）。"""

from __future__ import annotations

import argparse
import hashlib
import json
from datetime import datetime, timezone
from pathlib import Path


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def write_json(path: Path, data: dict) -> None:
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--dictionary", type=Path, required=True)
    parser.add_argument("--images", type=Path, required=True)
    parser.add_argument("--font", type=Path, help="可选：字体目录，生成字体及许可的远程清单")
    args = parser.parse_args()

    stamp = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")
    dictionary = args.dictionary.resolve()
    images = args.images.resolve()
    if not dictionary.is_file() or not images.is_dir():
        parser.error("dictionary and image directory must exist")

    json.loads(dictionary.read_text(encoding="utf-8"))
    files = {}
    for image in sorted(images.glob("*.png")):
        if not image.read_bytes().startswith(b"\x89PNG\r\n\x1a\n"):
            parser.error(f"not a PNG: {image}")
        files[image.name] = sha256(image)

    version_path = dictionary.with_suffix(".version.json")
    manifest_path = images / "manifest.json"

    def unchanged(path: Path, key: str, value: object) -> bool:
        try:
            return json.loads(path.read_text(encoding="utf-8"))[key] == value
        except (OSError, ValueError, KeyError):
            return False

    dictionary_hash = sha256(dictionary)
    if not unchanged(version_path, "sha256", dictionary_hash):
        write_json(version_path, {"updatedAt": stamp, "sha256": dictionary_hash})
    if not unchanged(manifest_path, "files", files):
        write_json(manifest_path, {"updatedAt": stamp, "files": files})
    if args.font:
        font_dir = args.font.resolve()
        font_files = ["NotoSerifCJKtc-SemiBold.otf", "OFL-NotoCJK.txt"]
        for name in font_files:
            if not (font_dir / name).is_file():
                parser.error(f"missing font distribution file: {font_dir / name}")
        font_hashes = {name: sha256(font_dir / name) for name in font_files}
        font_manifest = font_dir / "manifest.json"
        if not unchanged(font_manifest, "files", font_hashes):
            write_json(font_manifest, {"updatedAt": stamp, "files": font_hashes})
    print(f"{stamp}: dictionary={dictionary.name}, images={len(files)}")


if __name__ == "__main__":
    main()
