"""Re-encode the game's desktop textures for the iPhone (A16: ASTC LDR + ASTC HDR) and rebuild the .resS streams.

What changes (reported per texture in convert_report.json):
  * BC1/BC3/BC4/BC5/BC7 -> ASTC LDR (sRGB textures with the sRGB profile).
  * BC6H and the RGBAHalf lightmaps -> ASTC HDR (decoded at full float precision with bc6hdec.dll;
    texture2ddecoder would clamp every lightmap value above 1.0).
  * Anything larger than MAX_SIZE (the 4096x4096 lightmaps) loses its top mip level(s): 4096 -> 2048.
  * Block size: mipmapped textures of 1024 px and more use 6x6 blocks (0.44 bytes/pixel instead of 1),
    smaller ones and UI / sprite textures without mipmaps keep 4x4 (sharpest).
  * Crunched DXT1/DXT5 (Metal can't sample DXT at all, and Unity's software fallback fails on the 1x1 mips):
    the base level is uncrunched and decoded, smaller mips are box-filtered from it.
RGBA32 / RGB24 / Alpha8 and other uncompressed textures are left alone.

Every texture's pixels live in the "<file>.resS" stream; the stream is rewritten with the new data and every
object that points into it (textures, meshes) gets its offset updated.
"""
import ctypes, os
import numpy as np
import astc_encoder as ae
import texture2ddecoder

HERE = os.path.dirname(os.path.abspath(__file__))
MAX_SIZE = 2048
BIG = 1024
# Unity TextureFormat ids
RGBA_HALF, DXT1, DXT5, BC6H, BC7, BC4, BC5 = 17, 10, 12, 24, 25, 26, 27
DXT1_CRUNCHED, DXT5_CRUNCHED = 28, 29
CRUNCHED = {DXT1_CRUNCHED: DXT1, DXT5_CRUNCHED: DXT5}
ASTC_LDR = {4: 48, 5: 49, 6: 50, 8: 51}
ASTC_HDR = {4: 66, 5: 67, 6: 68, 8: 69}
BLOCK_BYTES = {DXT1: 8, DXT5: 16, BC6H: 16, BC7: 16, BC4: 8, BC5: 16}
LDR_DECODERS = {DXT1: texture2ddecoder.decode_bc1, DXT5: texture2ddecoder.decode_bc3, BC7: texture2ddecoder.decode_bc7,
                BC4: texture2ddecoder.decode_bc4, BC5: texture2ddecoder.decode_bc5}
TEXTURE_CLASSES = ('Texture2D', 'Cubemap')
THREADS = max(1, (os.cpu_count() or 4) // 3)

_bc6 = None
_ctx = {}


def _bc6dll():
    global _bc6
    if _bc6 is None:
        cands = [os.environ.get('BC6HDEC_DLL') or '', os.path.join(HERE, 'bc6hdec', 'build', 'Release', 'bc6hdec.dll'),
                 os.path.join(os.path.dirname(os.path.dirname(HERE)), 'vigil-ios-port', 'tools', 'bc6hdec', 'build', 'Release', 'bc6hdec.dll')]
        p = next((c for c in cands if c and os.path.exists(c)), cands[1])
        if not os.path.exists(p):
            raise RuntimeError('bc6hdec.dll missing - build tools/bc6hdec with CMake first (see README)')
        _bc6 = ctypes.CDLL(p)
        _bc6.bc6h_to_rgba16f.argtypes = [ctypes.c_char_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_void_p]
    return _bc6


def _context(profile, block):
    k = (profile, block)
    if k not in _ctx:
        cfg = ae.ASTCConfig(profile, block, block, 1, quality=ae.ASTCQualityPreset.MEDIUM)
        _ctx[k] = ae.ASTCContext(cfg, THREADS)
    return _ctx[k]


def _blocks(n, b=4):
    return max(1, (n + b - 1) // b)


def _mips(w, h, n):
    out = []
    for _ in range(n):
        out.append((w, h))
        w, h = max(1, w >> 1), max(1, h >> 1)
    return out


def _src_mip_bytes(fmt, w, h):
    if fmt == RGBA_HALF:
        return w * h * 8
    return _blocks(w) * _blocks(h) * BLOCK_BYTES[fmt]


def _decode(fmt, data, w, h):
    """-> (numpy HxWx4 array cropped to w x h, astc type)."""
    pw, ph = _blocks(w) * 4, _blocks(h) * 4
    if fmt == RGBA_HALF:
        return np.frombuffer(data, np.uint16).reshape(h, w, 4), ae.ASTCType.F16
    if fmt == BC6H:
        out = np.empty((ph, pw, 4), np.uint16)
        _bc6dll().bc6h_to_rgba16f(bytes(data), pw, ph, 0, out.ctypes.data)
        return out[:h, :w], ae.ASTCType.F16
    bgra = np.frombuffer(LDR_DECODERS[fmt](bytes(data), pw, ph), np.uint8).reshape(ph, pw, 4)
    return bgra[:h, :w, [2, 1, 0, 3]], ae.ASTCType.U8


def plan(tt):
    """-> dict(block, hdr, drop) or None when the texture stays as it is."""
    fmt = tt.get('m_TextureFormat')
    name = tt.get('m_Name', '')
    hdr = fmt == BC6H or (fmt == RGBA_HALF and name.startswith('Lightmap-'))
    if fmt not in BLOCK_BYTES and fmt not in CRUNCHED and not hdr:
        return None
    w, h, mips = tt['m_Width'], tt['m_Height'], max(1, tt.get('m_MipCount', 1))
    drop = 0
    while max(w >> drop, h >> drop) > MAX_SIZE and mips - drop > 1:
        drop += 1
    nw, nh, nm = max(1, w >> drop), max(1, h >> drop), mips - drop
    block = 6 if (max(nw, nh) >= BIG and nm > 1) else 4
    return dict(fmt=fmt, block=block, hdr=hdr, drop=drop, w=nw, h=nh, mips=nm)


def transcode(tt, data, p):
    """Re-encode one texture: returns the new image bytes; tt is updated in place (format, size, mips)."""
    fmt, block = p['fmt'], p['block']
    w0, h0 = tt['m_Width'], tt['m_Height']
    mips0 = max(1, tt.get('m_MipCount', 1))
    images = max(1, tt.get('m_ImageCount', 1))
    sizes = _mips(w0, h0, mips0)
    if fmt in CRUNCHED:
        return _transcode_crunched(tt, data, p, sizes)
    per_image = sum(_src_mip_bytes(fmt, mw, mh) for mw, mh in sizes)
    if len(data) < per_image * images:
        raise ValueError('%s: %d bytes < %d x %d' % (tt.get('m_Name'), len(data), images, per_image))
    if p['hdr']:
        profile = ae.ASTCProfile.HDR
    else:
        profile = ae.ASTCProfile.LDR_SRGB if tt.get('m_ColorSpace') == 1 else ae.ASTCProfile.LDR
    ctx = _context(profile, block)
    swz = ae.ASTCSwizzle.from_str('RGBA')
    out = bytearray()
    for i in range(images):
        pos = i * per_image
        for level, (mw, mh) in enumerate(sizes):
            n = _src_mip_bytes(fmt, mw, mh)
            if level >= p['drop']:
                img, typ = _decode(fmt, data[pos:pos + n], mw, mh)
                enc = ctx.compress(ae.ASTCImage(typ, mw, mh, 1, np.ascontiguousarray(img).tobytes()), swz)
                expect = _blocks(mw, block) * _blocks(mh, block) * 16
                if len(enc) != expect:
                    raise ValueError('astc size %d != %d for %dx%d' % (len(enc), expect, mw, mh))
                out += enc
            pos += n
    tt['m_TextureFormat'] = (ASTC_HDR if p['hdr'] else ASTC_LDR)[block]
    tt['m_Width'], tt['m_Height'], tt['m_MipCount'] = p['w'], p['h'], p['mips']
    tt['m_CompleteImageSize'] = len(out) // images
    if 'm_PlatformBlob' in tt:
        tt['m_PlatformBlob'] = []
    return bytes(out)


def _transcode_crunched(tt, data, p, sizes):
    base = texture2ddecoder.unpack_unity_crunch(bytes(data))
    w0, h0 = sizes[0]
    img, typ = _decode(CRUNCHED[p['fmt']], base, w0, h0)
    img = img.astype(np.float32)
    profile = ae.ASTCProfile.LDR_SRGB if tt.get('m_ColorSpace') == 1 else ae.ASTCProfile.LDR
    ctx = _context(profile, p['block'])
    swz = ae.ASTCSwizzle.from_str('RGBA')
    out = bytearray()
    for level, (mw, mh) in enumerate(sizes):
        if level:
            ph, pw = img.shape[0], img.shape[1]
            img = img[:ph - ph % 2 or ph, :pw - pw % 2 or pw]
            if img.shape[0] > 1:
                img = (img[0::2] + img[1::2]) / 2
            if img.shape[1] > 1:
                img = (img[:, 0::2] + img[:, 1::2]) / 2
        if level >= p['drop']:
            u8 = np.clip(img + 0.5, 0, 255).astype(np.uint8)
            out += ctx.compress(ae.ASTCImage(ae.ASTCType.U8, mw, mh, 1, np.ascontiguousarray(u8).tobytes()), swz)
    tt['m_TextureFormat'] = ASTC_LDR[p['block']]
    tt['m_Width'], tt['m_Height'], tt['m_MipCount'] = p['w'], p['h'], p['mips']
    tt['m_CompleteImageSize'] = len(out)
    if 'm_PlatformBlob' in tt:
        tt['m_PlatformBlob'] = []
    return bytes(out)


def describe(p, tt_before):
    s = 'BC%s' % {DXT1: '1', DXT5: '3', BC4: '4', BC5: '5', BC6H: '6H', BC7: '7', DXT1_CRUNCHED: '1 crunched', DXT5_CRUNCHED: '3 crunched'}.get(p['fmt'], '?') if p['fmt'] != RGBA_HALF else 'RGBAHalf'
    t = 'ASTC%s %dx%d' % (' HDR' if p['hdr'] else '', p['block'], p['block'])
    size = '' if not p['drop'] else ' %dx%d->%dx%d' % (tt_before[0], tt_before[1], p['w'], p['h'])
    return '%s -> %s%s' % (s, t, size)


def transcode_file(sf, src_data_dir, dst_data_dir, report):
    """Transcode every texture of the serialized file sf and write its rebuilt .resS file(s) to dst_data_dir.
    Objects whose stream data moved get their m_StreamData updated (saved into sf, which the caller writes)."""
    texlog = report.setdefault('texture_changes', {})
    errors = report.setdefault('texture_errors', [])
    streams = {}       # resS name -> [(old offset, size, obj, tt)]
    new_data = {}      # path_id -> new bytes (moved into the stream)
    for obj in sf.objects.values():
        if obj.type.name not in TEXTURE_CLASSES and obj.type.name not in ('Mesh', 'Texture3D', 'Texture2DArray', 'CubemapArray'):
            continue
        tt = obj.read_typetree()
        sd = tt.get('m_StreamData')
        if sd and sd.get('size'):
            streams.setdefault(sd['path'], []).append((sd['offset'], sd['size'], obj, tt))
        if obj.type.name not in TEXTURE_CLASSES:
            continue
        p = plan(tt)
        if not p:
            continue
        try:
            if sd and sd.get('size'):
                with open(os.path.join(src_data_dir, sd['path']), 'rb') as f:
                    f.seek(sd['offset'])
                    data = f.read(sd['size'])
            else:
                data = bytes(tt['image data'])
            before = (tt['m_Width'], tt['m_Height'])
            out = transcode(tt, data, p)
            texlog[tt.get('m_Name', str(obj.path_id))] = describe(p, before)
            if sd and sd.get('size'):
                new_data[obj.path_id] = out
            else:
                tt['image data'] = out
                obj.save_typetree(tt)
        except Exception as e:
            errors.append('%s: %r' % (tt.get('m_Name'), e))
    n = 0
    for path, items in streams.items():
        src = os.path.join(src_data_dir, path)
        dst = os.path.join(dst_data_dir, path)
        os.makedirs(os.path.dirname(dst), exist_ok=True)
        if not any(o.path_id in new_data for _, _, o, _ in items):
            continue                       # untouched stream: the caller copies it as is
        items.sort(key=lambda x: x[0])
        with open(src, 'rb') as fi, open(dst, 'wb') as fo:
            for off, size, obj, tt in items:
                if obj.path_id in new_data:
                    blob = new_data[obj.path_id]
                    n += 1
                else:
                    fi.seek(off)
                    blob = fi.read(size)
                pad = (-fo.tell()) % 16
                fo.write(b'\0' * pad)
                tt['m_StreamData'] = dict(offset=fo.tell(), size=len(blob), path=path)
                fo.write(blob)
                obj.save_typetree(tt)
        report.setdefault('rebuilt_streams', []).append(path)
    return n
