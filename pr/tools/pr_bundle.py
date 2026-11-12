"""Read the text assets (CSV, TXT, JSON, BYTES) out of a Pixel Remaster's asset bundles with
Python's standard library alone: what a mod's setup runs on a player's machine, where the
bundled Python has no UnityPy (tools/extract.py, which has, makes pr/vanilla on this PC).

    python tools/pr_bundle.py <bundle> [<bundle> ...]       list each bundle's text assets
    from pr_bundle import text_assets
    for container_path, data in text_assets(bundle_path): ...

Unity 2019.4's formats, as the PR games ship them: a UnityFS archive (LZ4/LZ4HC or LZMA blocks)
holding one SerializedFile; its AssetBundle object's m_Container names each TextAsset (class 49).
The bytes are the ones tools/extract.py writes (checked file by file against pr/vanilla).
"""
import lzma
import struct
import sys


def lz4_block(src, size):
    """One LZ4 block (no frame), `size` bytes out."""
    dst = bytearray()
    i, n = 0, len(src)
    while i < n:
        token = src[i]
        i += 1
        lit = token >> 4
        if lit == 15:
            while True:
                b = src[i]
                i += 1
                lit += b
                if b != 255:
                    break
        dst += src[i:i + lit]
        i += lit
        if i >= n:
            break
        off = src[i] | (src[i + 1] << 8)
        i += 2
        ml = token & 15
        if ml == 15:
            while True:
                b = src[i]
                i += 1
                ml += b
                if b != 255:
                    break
        ml += 4
        start = len(dst) - off
        if off <= 0 or start < 0:
            raise ValueError("lz4: bad match offset")
        if off >= ml:
            dst += dst[start:start + ml]
        else:   # the match overlaps what it writes: the last `off` bytes, repeated
            chunk = bytes(dst[start:])
            reps, rest = divmod(ml, off)
            dst += chunk * reps + chunk[:rest]
    if len(dst) != size:
        raise ValueError(f"lz4: {len(dst)} bytes, expected {size}")
    return bytes(dst)


def decompress(data, size, kind):
    if kind == 0:
        return bytes(data)
    if kind in (2, 3):
        return lz4_block(data, size)
    if kind == 1:   # LZMA: 5 property bytes, then the raw stream
        d = data[0]
        lc, d = d % 9, d // 9
        lp, pb = d % 5, d // 5
        dict_size = struct.unpack_from("<I", data, 1)[0]
        dec = lzma.LZMADecompressor(lzma.FORMAT_RAW, filters=[
            {"id": lzma.FILTER_LZMA1, "lc": lc, "lp": lp, "pb": pb, "dict_size": dict_size}])
        return dec.decompress(bytes(data[5:]), max_length=size)
    raise ValueError(f"unknown compression {kind}")


def _cstr(b, i):
    j = b.index(b"\0", i)
    return bytes(b[i:j]), j + 1


class Bundle:
    """A UnityFS archive: its files (nodes) by name, read on demand."""

    def __init__(self, path):
        with open(path, "rb") as f:
            self.raw = f.read()
        r = self.raw
        sig, i = _cstr(r, 0)
        if sig != b"UnityFS":
            raise ValueError(f"{path}: not a UnityFS bundle ({sig[:16]!r})")
        version = struct.unpack_from(">I", r, i)[0]
        i += 4
        _, i = _cstr(r, i)          # "5.x.x"
        _, i = _cstr(r, i)          # the engine's version
        _, cbi, ubi, flags = struct.unpack_from(">qIII", r, i)
        i += 20
        if flags & 0x80:            # blocks info at the end
            at = [len(r) - cbi]
        else:                       # version 7 headers are 16-aligned (older ones aren't)
            at = [(i + 15) & ~15, i] if version >= 7 else [i]
        info, start = None, None
        for a in at:
            try:
                info = decompress(r[a:a + cbi], ubi, flags & 0x3F)
                start = a + cbi
                break
            except (ValueError, IndexError, lzma.LZMAError):
                continue
        if info is None:
            raise ValueError(f"{path}: unreadable blocks info")
        if flags & 0x80:
            start = (i + 15) & ~15 if version >= 7 else i
        if flags & 0x200:
            start = (start + 15) & ~15
        j = 16                       # the uncompressed data's hash
        (count,) = struct.unpack_from(">i", info, j)
        j += 4
        self.blocks = []            # (file offset, compressed size, size, compression, stream offset)
        pos, upos = start, 0
        for _ in range(count):
            usize, csize, bflags = struct.unpack_from(">IIH", info, j)
            j += 10
            self.blocks.append((pos, csize, usize, bflags & 0x3F, upos))
            pos += csize
            upos += usize
        (count,) = struct.unpack_from(">i", info, j)
        j += 4
        self.nodes = {}
        for _ in range(count):
            off, size, _nflags = struct.unpack_from(">qqI", info, j)
            j += 20
            name, j = _cstr(info, j)
            self.nodes[name.decode("utf-8", "replace")] = (off, size)
        self._cache = {}

    def _block(self, k):
        if k not in self._cache:
            pos, csize, usize, kind, _ = self.blocks[k]
            self._cache[k] = decompress(memoryview(self.raw)[pos:pos + csize], usize, kind)
        return self._cache[k]

    def read(self, off, size):
        out = bytearray()
        for k, (_, _, usize, _, upos) in enumerate(self.blocks):
            if upos + usize <= off or upos >= off + size:
                continue
            b = self._block(k)
            out += b[max(0, off - upos):min(usize, off + size - upos)]
        return bytes(out)

    def file(self, name):
        off, size = self.nodes[name]
        return self.read(off, size)

    def serialized_files(self):
        return [n for n in self.nodes if not n.endswith((".resS", ".resource"))]


class SerializedFile:
    """Its objects: path id -> (class id, data offset, size)."""

    def __init__(self, data):
        self.data = data
        _, _, version, data_offset = struct.unpack_from(">IIII", data, 0)
        i = 16
        endian = data[i] if version >= 9 else 1
        i += 4
        if version >= 22:
            _, _, data_offset, _ = struct.unpack_from(">IqqQ", data, i)
            i += 28
        e = "<" if endian == 0 else ">"
        self.e = e
        self.version = version
        _, i = _cstr(data, i)                               # the engine's version
        i += 4                                              # target platform
        tree = True
        if version >= 13:
            tree = bool(data[i])
            i += 1
        (count,) = struct.unpack_from(e + "i", data, i)
        i += 4
        classes = []
        for _ in range(count):
            (cid,) = struct.unpack_from(e + "i", data, i)
            i += 4
            if version >= 16:
                i += 1                                      # stripped
            if version >= 17:
                i += 2                                      # script type index
            if version >= 13:
                if (version < 16 and cid < 0) or (version >= 16 and cid == 114):
                    i += 16                                 # script id
                i += 16                                     # old type hash
            if tree:
                if version < 12 and version != 10:
                    raise ValueError(f"serialized file version {version}: old type trees aren't read")
                nodes, strings = struct.unpack_from(e + "ii", data, i)
                i += 8 + nodes * (32 if version >= 19 else 24) + strings
                if version >= 21:
                    (deps,) = struct.unpack_from(e + "i", data, i)
                    i += 4 + 4 * deps
            classes.append(cid)
        if 7 <= version < 14:
            i += 4
        (count,) = struct.unpack_from(e + "i", data, i)
        i += 4
        self.objects = {}
        for _ in range(count):
            if version >= 14:
                i = (i + 3) & ~3
            (path_id,) = struct.unpack_from(e + "q", data, i)
            i += 8
            if version >= 22:
                (start,) = struct.unpack_from(e + "q", data, i)
                i += 8
            else:
                (start,) = struct.unpack_from(e + "I", data, i)
                i += 4
            size, type_id = struct.unpack_from(e + "Ii", data, i)
            i += 8
            if version < 16:
                (cid,) = struct.unpack_from(e + "H", data, i)
                i += 2
            else:
                cid = classes[type_id]
            if version < 11:
                i += 2
            if 11 <= version < 17:
                i += 2
            if version in (15, 16):
                i += 1
            self.objects[path_id] = (cid, data_offset + start, size)

    def reader(self, path_id):
        _, start, size = self.objects[path_id]
        return _Reader(self.data, start, start + size, self.e)


class _Reader:
    def __init__(self, data, start, end, e):
        self.d, self.i, self.start, self.end, self.e = data, start, start, end, e

    def i32(self):
        (v,) = struct.unpack_from(self.e + "i", self.d, self.i)
        self.i += 4
        return v

    def i64(self):
        (v,) = struct.unpack_from(self.e + "q", self.d, self.i)
        self.i += 8
        return v

    def blob(self):
        n = self.i32()
        if n < 0 or self.i + n > self.end:
            raise ValueError("bad length")
        v = self.d[self.i:self.i + n]
        self.i += n
        self.i = self.start + ((self.i - self.start + 3) & ~3)
        return v


def text_assets(path):
    """(container path, bytes) for each TextAsset the bundle names, in its container's order."""
    bundle = Bundle(path)
    out = []
    for name in bundle.serialized_files():
        sf = SerializedFile(bundle.file(name))
        for pid, (cid, _, _) in sf.objects.items():
            if cid != 142:          # AssetBundle: its container names the assets
                continue
            r = sf.reader(pid)
            r.blob()                                    # m_Name
            preload = r.i32()                           # m_PreloadTable: PPtr (file id, path id)
            r.i += 12 * preload
            container = {}
            for _ in range(r.i32()):
                key = r.blob().decode("utf-8", "surrogateescape")
                r.i += 8                                # preload index, size
                file_id, asset = r.i32(), r.i64()
                if file_id == 0:
                    container[key] = asset
            for key, asset in container.items():
                if sf.objects.get(asset, (None,))[0] != 49:   # TextAsset
                    continue
                t = sf.reader(asset)
                t.blob()                                # m_Name
                out.append((key, bytes(t.blob())))      # m_Script
    return out


if __name__ == "__main__":
    for p in sys.argv[1:]:
        for key, data in text_assets(p):
            print(f"{len(data):9}  {key}")
