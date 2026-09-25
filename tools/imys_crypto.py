"""
IMYS 资源包解密。

游戏缓存目录 `imys_r_Data/Caches/assetbundles/**/*.encrypted` 的文件结构：

    [uint32 LE 明文长度][AES-256-CBC 密文]

密文 = AES-CBC( IV || 明文 )，且 **IV 存在密文的首个 block**（用全零 IV 解密后丢掉前 16 字节即得明文），
因此先解密再切片 `[16:]`。

密钥：
    aeskey = HMAC-SHA256(key=HmacKey, msg=DataKey)

来源：密钥与算法复刻自 `irisProject/iris_resource_dump.py`
（`aeskey = hmac.new(HmacKey, DataKey, digestmod=sha256).digest()` /
 `AES.new(aeskey, AES.MODE_CBC).decrypt(data)[16:]`），
并用 `commonlang.jp.encrypted` → `UnityFS` 魔数验证通过。
"""

from __future__ import annotations

import hashlib
import hmac
import struct
from pathlib import Path

from Crypto.Cipher import AES

HMAC_KEY = b"dB3aqcLtAmBd"
DATA_KEY = b"RWd3NusabzRc"

#: 游戏根目录的相对资源缓存路径
CACHE_REL = Path("imys_r_Data/Caches/assetbundles")


def aes_key() -> bytes:
    return hmac.new(HMAC_KEY, DATA_KEY, hashlib.sha256).digest()


def decrypt_bytes(blob: bytes) -> bytes:
    """解密单个 .encrypted 文件的全部字节。"""
    if len(blob) < 4:
        raise ValueError("file too small")
    (declared,) = struct.unpack("<I", blob[:4])
    body = blob[4 : 4 + declared]
    if len(body) % 16:
        raise ValueError(f"payload not 16-byte aligned: {len(body)}")
    return AES.new(aes_key(), AES.MODE_CBC).decrypt(body)[16:]


def decrypt_to(src: str | Path, dst: str | Path) -> int:
    src, dst = Path(src), Path(dst)
    dst.parent.mkdir(parents=True, exist_ok=True)
    data = decrypt_bytes(src.read_bytes())
    dst.write_bytes(data)
    return len(data)


def find_game_root(start: str | Path | None = None) -> Path:
    """从当前目录向上找含 imys_r_Data 的游戏根目录。"""
    cur = Path(start or Path.cwd()).resolve()
    for cand in (cur, *cur.parents):
        if (cand / CACHE_REL).is_dir():
            return cand
    raise SystemExit("找不到游戏根目录（需要包含 imys_r_Data/Caches/assetbundles），请用 --game 指定")
