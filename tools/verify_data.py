"""Sanity check of a converted VigilData folder (run after convert_data.py).

  python tools/verify_data.py [C:/Users/<you>/vigil-work/VigilData]

For every player file: it must load, be tagged iOS, contain no D3D11 shader programs / compute kernels, and
every texture / mesh stream reference must lie inside its .resS with the size its format needs.
Textures must not be in a desktop-only (BCn) format: iOS 16.2 GPUs cannot sample those.
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import UnityPy
from convert_data import PLAYER_FILES, IOS_PLATFORM
from transcode_tex import BLOCK_BYTES, BC6H, RGBA_HALF, TEXTURE_CLASSES, _mips

UnityPy.config.FALLBACK_UNITY_VERSION = '6000.0.26f1'
ASTC_BLOCK = {48: 4, 49: 5, 50: 6, 51: 8, 66: 4, 67: 5, 68: 6, 69: 8}
DESKTOP_ONLY = set(BLOCK_BYTES) | {BC6H, 28, 29}   # BCn + DXT1/5 crunched


def astc_size(fmt, w, h, mips):
    b = ASTC_BLOCK[fmt]
    return sum(((mw + b - 1) // b) * ((mh + b - 1) // b) * 16 for mw, mh in _mips(w, h, mips))


def check(path):
    errs, n_tex, n_mesh = [], 0, 0
    sf = list(UnityPy.load(path).files.values())[0]
    if sf._m_target_platform != IOS_PLATFORM:
        errs.append('platform %d' % sf._m_target_platform)
    folder = os.path.dirname(path)
    for obj in sf.objects.values():
        kind = obj.type.name
        if kind == 'Shader':
            tt = obj.read_typetree()
            if 4 in tt['platforms']:
                errs.append('shader %s still has D3D11' % tt['m_ParsedForm']['m_Name'])
        elif kind == 'ComputeShader':
            tt = obj.read_typetree()
            if any(v['targetRenderer'] == 2 for v in tt['variants']):
                errs.append('compute %s still has D3D11' % tt['m_Name'])
        elif kind in TEXTURE_CLASSES or kind == 'Mesh':
            tt = obj.read_typetree()
            sd = tt.get('m_StreamData')
            name = tt.get('m_Name')
            if sd and sd.get('size'):
                p = os.path.join(folder, sd['path'])
                if not os.path.exists(p):
                    errs.append('%s: stream %s missing' % (name, sd['path']))
                elif sd['offset'] + sd['size'] > os.path.getsize(p):
                    errs.append('%s: stream past end of %s' % (name, sd['path']))
            if kind == 'Mesh':
                n_mesh += 1
                continue
            n_tex += 1
            fmt = tt['m_TextureFormat']
            if fmt in DESKTOP_ONLY:
                errs.append('%s: still desktop format %d' % (name, fmt))
            if fmt in ASTC_BLOCK:
                need = astc_size(fmt, tt['m_Width'], tt['m_Height'], max(1, tt['m_MipCount'])) * max(1, tt.get('m_ImageCount', 1))
                have = sd['size'] if sd and sd.get('size') else len(tt['image data'])
                if have != need:
                    errs.append('%s: %d bytes, ASTC needs %d' % (name, have, need))
    return errs, n_tex, n_mesh


def main():
    root = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.expanduser('~'), 'vigil-work', 'VigilData')
    bad = 0
    for f in PLAYER_FILES:
        p = os.path.join(root, f)
        if not os.path.exists(p):
            print('%-26s MISSING' % f)
            bad += 1
            continue
        errs, nt, nm = check(p)
        print('%-26s textures %4d meshes %4d  %s' % (f, nt, nm, 'OK' if not errs else '%d PROBLEMS' % len(errs)))
        for e in errs[:20]:
            print('    ', e)
        bad += bool(errs)
    print('ALL OK' if not bad else '%d file(s) with problems' % bad)
    sys.exit(1 if bad else 0)


if __name__ == '__main__':
    main()
