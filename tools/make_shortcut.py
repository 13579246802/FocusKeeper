"""生成桌面快捷方式（.lnk），不依赖 COM/WScript.Shell。

用法：python tools/make_shortcut.py <exe 路径> <快捷方式路径>
只写 ShellLinkHeader + LinkInfo（VolumeIDAndLocalBasePath），够 Windows 解析目标。
"""
import re
import struct
import sys

HEADER_SIZE = 0x4C


def win(path: str) -> str:
    """把 d:/a//b 之类的写法收敛成 d:\\a\\b（Git Bash 会篡改反斜杠参数）。"""
    p = re.sub(r'[\\/]+', r'\\', path)
    if re.match(r'^[A-Za-z]:\\', p):
        return p
    return p
CLSID_SHELL_LINK = bytes([0x01, 0x14, 0x02, 0x00, 0, 0, 0, 0,
                          0xC0, 0x00, 0, 0, 0, 0, 0, 0x46])
FLAG_HAS_LINK_INFO = 0x00000002
FLAG_IS_UNICODE = 0x00000080
DRIVE_FIXED = 3


def build(target: str) -> bytes:
    hdr = struct.pack('<I', HEADER_SIZE) + CLSID_SHELL_LINK
    hdr += struct.pack('<I', FLAG_HAS_LINK_INFO | FLAG_IS_UNICODE)
    hdr += struct.pack('<I', 0x00000020)      # FILE_ATTRIBUTE_ARCHIVE
    hdr += struct.pack('<QQQ', 0, 0, 0)       # 创建/访问/修改时间留空
    hdr += struct.pack('<I', 0)               # FileSize
    hdr += struct.pack('<i', 0)               # IconIndex（0 = 用目标自身图标）
    hdr += struct.pack('<I', 1)               # ShowCommand = SW_SHOWNORMAL
    hdr += struct.pack('<H', 0)               # HotKey
    hdr += struct.pack('<H', 0)               # Reserved1
    hdr += struct.pack('<I', 0)               # Reserved2
    hdr += struct.pack('<I', 0)               # Reserved3
    assert len(hdr) == HEADER_SIZE

    local = target.encode('mbcs') + b'\x00'
    suffix = b'\x00'
    label = b'\x00'
    vol_size = 16 + len(label)
    vol = struct.pack('<IIII', vol_size, DRIVE_FIXED, 0, 0x10) + label

    vol_off = 0x1C
    local_off = vol_off + vol_size
    suffix_off = local_off + len(local)
    info_size = suffix_off + len(suffix)

    info = struct.pack('<IIIIIII', info_size, 0x1C, 0x1,
                       vol_off, local_off, 0, suffix_off)
    return hdr + info + vol + local + suffix


def read_back(path: str) -> str:
    raw = open(path, 'rb').read()
    assert struct.unpack_from('<I', raw, 0)[0] == HEADER_SIZE, '头部长度不对'
    size, _hs, _lf, _vo, lo, _cn, _so = struct.unpack_from('<IIIIIII', raw, 0x4C)
    assert size == len(raw) - 0x4C, 'LinkInfo 长度不对'
    end = raw.index(b'\x00', 0x4C + lo)
    return raw[0x4C + lo:end].decode('mbcs')


if __name__ == '__main__':
    target = win(sys.argv[1])
    out = win(sys.argv[2])
    open(out, 'wb').write(build(target))
    got = read_back(out)
    print(f'written {out}')
    print(f'target  {got}')
    print('OK' if got == target else 'MISMATCH')
