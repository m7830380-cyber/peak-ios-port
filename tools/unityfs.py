"""Minimal reader for a big UnityFS bundle (PEAK ships its whole player data as one 1.5 GB data.unity3d, LZ4HC).

Only the header and block table are read up front; a node (a file inside the bundle) is extracted by
decompressing just the blocks it covers, so nothing close to the bundle's 4.5 GB unpacked size ever has to be
in memory or on disk.
"""
import struct
import lz4.block


def _cstr(f):
    b = bytearray()
    while True:
        c = f.read(1)
        if not c or c == b'\0':
            return b.decode()
        b += c


class UnityFS:
    def __init__(self, path):
        self.path = path
        f = self.f = open(path, 'rb')
        sig = _cstr(f)
        if sig != 'UnityFS':
            raise ValueError('%s is not a UnityFS bundle (%r)' % (path, sig))
        ver = struct.unpack('>I', f.read(4))[0]
        _cstr(f)
        self.unity_version = _cstr(f)
        size, csize, usize, flags = struct.unpack('>qIII', f.read(20))
        if ver >= 7:
            f.seek((f.tell() + 15) // 16 * 16)
        data_start = f.tell()
        if flags & 0x80:                       # block info at the end
            f.seek(size - csize)
        else:
            data_start += csize
        raw = f.read(csize)
        ct = flags & 0x3f
        info = lz4.block.decompress(raw, uncompressed_size=usize) if ct in (2, 3) else raw
        if flags & 0x200:                      # padding before the first block
            data_start = (data_start + 15) // 16 * 16
        o = 16
        nb = struct.unpack_from('>i', info, o)[0]; o += 4
        self.blocks = []                       # (uncompressed offset, usize, file offset, csize, compression)
        uo, co = 0, data_start
        for _ in range(nb):
            u, c, fl = struct.unpack_from('>IIH', info, o); o += 10
            self.blocks.append((uo, u, co, c, fl & 0x3f))
            uo += u; co += c
        nn = struct.unpack_from('>i', info, o)[0]; o += 4
        self.nodes = {}                        # name -> (offset, size)
        self.order = []
        for _ in range(nn):
            off, sz, fl = struct.unpack_from('>qqI', info, o); o += 20
            e = info.index(b'\0', o)
            name = info[o:e].decode(); o = e + 1
            self.nodes[name] = (off, sz)
            self.order.append(name)

    def size(self, name):
        return self.nodes[name][1]

    def extract(self, name, out_path):
        """Writes node `name` to out_path, block by block."""
        off, sz = self.nodes[name]
        end = off + sz
        with open(out_path, 'wb') as w:
            for uo, u, co, c, ct in self.blocks:
                if uo + u <= off or uo >= end:
                    continue
                self.f.seek(co)
                raw = self.f.read(c)
                d = raw if ct == 0 else lz4.block.decompress(raw, uncompressed_size=u)
                a, b = max(off, uo) - uo, min(end, uo + u) - uo
                w.write(d[a:b])

    def close(self):
        self.f.close()
