"""只读查看指定 AssetBundle 的对象类型及 Sprite/Texture 名称。"""

import argparse
import io
import sys
from collections import Counter
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from imys_crypto import CACHE_REL, decrypt_bytes, find_game_root  # noqa: E402


def main():
    import UnityPy

    parser = argparse.ArgumentParser()
    parser.add_argument("bundles", nargs="+")
    args = parser.parse_args()
    cache = find_game_root() / CACHE_REL
    for rel in args.bundles:
        path = cache / rel
        env = UnityPy.load(io.BytesIO(decrypt_bytes(path.read_bytes())))
        print("\n", rel, Counter(obj.type.name for obj in env.objects))
        for obj in env.objects:
            if obj.type.name not in {"Sprite", "Texture2D", "TextAsset"}:
                continue
            data = obj.read()
            print(" ", obj.type.name, getattr(data, "m_Name", ""))


if __name__ == "__main__":
    main()
