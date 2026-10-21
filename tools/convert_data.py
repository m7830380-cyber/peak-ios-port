"""Convert the retail PEAK (Windows) data so the iOS player can load it.

  python tools/convert_data.py [--game "C:/Program Files (x86)/Steam/steamapps/common/PEAK"] [--work C:/Users/v0id/peak-work] [--only level1,...]

Produces <work>/data/Data/ - the files tools/package_ipa.py puts into the app's Data folder
(StreamingAssets go to Data/Raw, which is Application.streamingAssetsPath on iOS).
For every serialized player file (globalgamemanagers, levels, sharedassets, resources):
  * the target-platform tag changes from StandaloneWindows64 (19) to iOS (9);
  * every Shader gets its DX11 programs translated to Metal (convert_shader.py);
  * every ComputeShader gets its D3D11 kernels translated to Metal (convert_compute.py);
  * BCn textures become ASTC, BC6H / half-float lightmaps ASTC HDR, 4096 maps 2048 (transcode_tex.py);
  * .resS streams that hold re-encoded textures are rebuilt, all others are copied unchanged.
Addressables bundles (StreamingAssets/aa, the localization tables / images): same conversion, iOS tag, LZ4.
The binary catalog is copied unchanged (its paths are already joined with "/" at load time); the
patched Unity.ResourceManager ignores the bundle CRCs the converted bundles no longer match.
Resources/ is not produced: the iOS player brings its own "unity default resources" and unity_builtin_extra.

Everything produced here is derived from the game: never commit or upload it anywhere public.
"""
import argparse, hashlib, json, os, pickle, re, shutil, subprocess, sys, time, traceback
from concurrent.futures import ProcessPoolExecutor, as_completed

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import UnityPy
from convert_shader import convert_shader as convert_one
from convert_compute import convert_compute
import transcode_tex as tex
from transcode_tex import transcode_file

UnityPy.config.FALLBACK_UNITY_VERSION = '6000.0.67f1'
IOS_PLATFORM = 9
DEFAULT_GAME = r'C:\Program Files (x86)\Steam\steamapps\common\PEAK'
DEFAULT_WORK = r'C:\Users\v0id\peak-work'
DATA_DIR = 'PEAK_Data'
CONVERTER_VERSION = 'peak-1'   # bump to invalidate the shader cache


def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)


def player_files(data):
    """globalgamemanagers(.assets), levelN, resources.assets, sharedassetsN.assets - whatever the game ships."""
    out = []
    for f in os.listdir(data):
        p = os.path.join(data, f)
        if not os.path.isfile(p):
            continue
        if f in ('globalgamemanagers', 'globalgamemanagers.assets', 'resources.assets') or re.fullmatch(r'level\d+', f) \
                or re.fullmatch(r'sharedassets\d+\.assets', f):
            out.append(f)
    return out


def cache_key(kind, name, blob):
    h = hashlib.sha1()
    h.update(CONVERTER_VERSION.encode())
    h.update(kind.encode())
    h.update(name.encode())
    h.update(blob)
    return h.hexdigest()


def cached(cache_dir, key, make):
    cp = os.path.join(cache_dir, key + '.pkl')
    if os.path.exists(cp):
        with open(cp, 'rb') as f:
            return pickle.load(f)
    res = make()
    with open(cp, 'wb') as f:
        pickle.dump(res, f)
    return res


def convert_shaders_in(sf, cache_dir, work_dir, report):
    n = 0
    for obj in sf.objects.values():
        kind = obj.type.name
        if kind not in ('Shader', 'ComputeShader'):
            continue
        tt = obj.read_typetree()
        if kind == 'Shader':
            blob = bytes(tt['compressedBlob'])
            name = tt['m_ParsedForm']['m_Name']
        else:
            name = tt['m_Name']
            blob = pickle.dumps(tt['variants'])
        key = cache_key(kind, name, blob)
        wd = os.path.join(work_dir, 'hlslcc', key)
        msgs = []

        def make():
            if kind == 'Shader':
                tt['compressedBlob'] = b''
                r = convert_one(tt, blob, wd, log=msgs.append)
            else:
                r = convert_compute(tt, wd, log=msgs.append)
            shutil.rmtree(wd, ignore_errors=True)
            return r, msgs[:50]

        res, msgs = cached(cache_dir, key, make)
        if res is None:
            report.setdefault('no_dx11', []).append(name)
            continue
        nt, stats = res
        report.setdefault('compute_shaders' if kind == 'ComputeShader' else 'shaders', {})[name] = stats
        if stats.get('fail'):
            report.setdefault('shader_msgs', {})[name] = msgs
        obj.save_typetree(nt)
        n += 1
    return n


def streams_of(src):
    """Stream files that belong to a player file: <file>.resS and, for x.assets, x.resource."""
    out = []
    for p in (src + '.resS', os.path.splitext(src)[0] + '.resource', src + '.resource'):
        if os.path.exists(p) and p not in out:
            out.append(p)
    return out


def process_player_file(name, data, out_data, cache_dir, work_dir):
    t0 = time.time()
    report = {}
    src = os.path.join(data, name)
    env = UnityPy.load(src)
    sf = list(env.files.values())[0]
    report['shaders_converted'] = convert_shaders_in(sf, cache_dir, work_dir, report)
    report['textures_converted'] = transcode_file(sf, data, out_data, report)
    sf._m_target_platform = IOS_PLATFORM
    dst = os.path.join(out_data, name)
    with open(dst + '.tmp', 'wb') as f:
        f.write(sf.save())
    os.replace(dst + '.tmp', dst)
    rebuilt = set(report.get('rebuilt_streams', []))
    for s in streams_of(src):
        rel = os.path.relpath(s, data)
        if rel not in rebuilt:
            shutil.copyfile(s, os.path.join(out_data, rel))
    report['seconds'] = round(time.time() - t0)
    return report


def bundle_texture_bytes(bf, tt):
    """Stored image bytes of a texture in a bundle: inline, or in the bundle's own CAB-....resS."""
    sd = tt.get('m_StreamData')
    if not sd or not sd.get('size'):
        return bytes(tt['image data'])
    stream = sd['path'].split('/')[-1]               # archive:/CAB-x/CAB-x.resS
    f = bf.files.get(stream)
    if f is None:
        raise ValueError('stream %s not in bundle (%s)' % (stream, list(bf.files)))
    buf = f.bytes if hasattr(f, 'bytes') else f.read_bytes(f.Length)
    return bytes(buf[sd['offset']:sd['offset'] + sd['size']])


def process_bundle(src, dst, cache_dir, work_dir):
    """Addressables bundle: shaders + textures converted (texture data moved inline), iOS tag, LZ4."""
    report = {}
    env = UnityPy.load(src)
    bf = list(env.files.values())[0]
    for name, sf in bf.files.items():
        if not hasattr(sf, 'objects'):
            continue
        report['shaders'] = report.get('shaders', 0) + convert_shaders_in(sf, cache_dir, work_dir, report)
        for obj in sf.objects.values():
            if obj.type.name not in tex.TEXTURE_CLASSES:
                continue
            tt = obj.read_typetree()
            p = tex.plan(tt)
            if not p:
                continue
            try:
                raw = bundle_texture_bytes(bf, tt)
                before = (tt['m_Width'], tt['m_Height'])
                out = tex.transcode(tt, raw, p)
                tt['image data'] = out
                if tt.get('m_StreamData'):
                    tt['m_StreamData'] = dict(offset=0, size=0, path='')
                obj.save_typetree(tt)
                report.setdefault('texture_changes', {})[tt.get('m_Name', '')] = tex.describe(p, before)
            except Exception as e:
                report.setdefault('texture_errors', []).append('%s: %r' % (tt.get('m_Name'), e))
        sf._m_target_platform = IOS_PLATFORM
    data = bf.save(packer='lz4')
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    with open(dst + '.tmp', 'wb') as f:
        f.write(data)
    os.replace(dst + '.tmp', dst)
    return report


def job(kind, *args):
    try:
        import convert_shader
        convert_shader.INLINE_SEEN.clear()
        rep = (process_player_file if kind == 'player' else process_bundle)(*args)
        rep['inline_samplers'] = {str(k): v for k, v in convert_shader.INLINE_SEEN.items()}
        return rep
    except Exception:
        return {'error': traceback.format_exc()}


def fix_catalog(raw):
    """settings.json says iOS. catalog.bin stays byte-identical: it is a binary buffer (offsets, lengths, hashes)
    and its internal ids are joined with '/' at load time, so editing bytes in it corrupts it."""
    st = os.path.join(raw, 'aa', 'settings.json')
    if os.path.exists(st):
        s = json.load(open(st, encoding='utf8'))
        s['m_buildTarget'] = 'iOS'
        json.dump(s, open(st, 'w', encoding='utf8'), separators=(',', ':'))


def tree_size(p):
    return sum(os.path.getsize(os.path.join(r, f)) for r, _, fs in os.walk(p) for f in fs)


def webm_to_mp4(src, dst):
    """The iOS video player cannot play WebM (the bundled lost footage): re-encode to H.264 + AAC MP4."""
    ff = shutil.which('ffmpeg')
    if not ff:
        raise SystemExit('ffmpeg is needed to convert %s (iOS cannot play WebM)' % src)
    subprocess.run([ff, '-v', 'error', '-y', '-i', src, '-c:v', 'libx264', '-preset', 'slow', '-crf', '20',
                    '-pix_fmt', 'yuv420p', '-c:a', 'aac', '-b:a', '128k', '-movflags', '+faststart', dst], check=True)
    log('converted', os.path.basename(src), '->', os.path.basename(dst))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=DEFAULT_GAME)
    ap.add_argument('--work', default=DEFAULT_WORK)
    ap.add_argument('--only', default=None, help='comma separated player file / bundle names (testing)')
    ap.add_argument('--jobs', type=int, default=2)
    a = ap.parse_args()

    data = os.path.join(a.game, DATA_DIR)
    out_data = os.path.join(a.work, 'data', 'Data')
    raw = os.path.join(out_data, 'Raw')
    cache_dir = os.path.join(a.work, 'cache', 'shaders')
    work_dir = os.path.join(a.work, 'tmp')
    os.makedirs(cache_dir, exist_ok=True)
    os.makedirs(out_data, exist_ok=True)
    only = set(a.only.split(',')) if a.only else None

    files = [f for f in player_files(data) if not only or f in only]
    files.sort(key=lambda f: -sum(os.path.getsize(s) for s in [os.path.join(data, f)] + streams_of(os.path.join(data, f))))

    # StreamingAssets: bundles are converted below, everything else is copied
    sa = os.path.join(data, 'StreamingAssets')
    bundles = []
    if not only:
        shutil.rmtree(raw, ignore_errors=True)
    for root, _, fs in os.walk(sa):
        for f in fs:
            src = os.path.join(root, f)
            rel = os.path.relpath(src, sa)
            if f.endswith('.bundle'):
                if not only or f in only:
                    bundles.append((src, os.path.join(raw, rel)))
            elif not only:
                os.makedirs(os.path.dirname(os.path.join(raw, rel)), exist_ok=True)
                if f.endswith('.webm'):
                    webm_to_mp4(src, os.path.join(raw, rel[:-5] + '.mp4'))
                else:
                    shutil.copyfile(src, os.path.join(raw, rel))
    if not only:
        fix_catalog(raw)

    log('converting %d player files and %d bundles with %d jobs' % (len(files), len(bundles), a.jobs))
    rp = os.path.join(a.work, 'convert_report.json')
    report = json.load(open(rp)) if (only and os.path.exists(rp)) else {}
    t0 = time.time()
    ok = True
    with ProcessPoolExecutor(max_workers=a.jobs) as ex:
        futs = {ex.submit(job, 'player', f, data, out_data, cache_dir, work_dir): f for f in files}
        futs.update({ex.submit(job, 'bundle', s, d, cache_dir, work_dir): os.path.basename(s) for s, d in bundles})
        for fu in as_completed(futs):
            f = futs[fu]
            rep = fu.result()
            report[f] = rep
            if rep.get('error'):
                ok = False
                log('FAILED', f, rep['error'])
                continue
            sh = dict(rep.get('shaders', {}) if isinstance(rep.get('shaders'), dict) else {}, **rep.get('compute_shaders', {}))
            bad = sorted(n for n, s in sh.items() if s.get('fail'))
            if 'seconds' in rep:
                log('done %-28s %4ds  shaders %3d (with failures: %d)  textures %4d (errors %d)' % (
                    f, rep['seconds'], rep['shaders_converted'], len(bad), rep['textures_converted'],
                    len(rep.get('texture_errors', []))))
            elif rep.get('texture_changes') or rep.get('texture_errors'):
                log('done bundle %s: textures %d (errors %d)' % (f, len(rep.get('texture_changes', {})), len(rep.get('texture_errors', []))))
            json.dump(report, open(rp, 'w'), indent=1)
    json.dump(report, open(rp, 'w'), indent=1)
    seen = {}
    for r in report.values():
        for k, v in r.get('inline_samplers', {}).items():
            seen[k] = seen.get(k, 0) + v
    log('inline sampler flags seen (flags: uses):', seen)
    shutil.rmtree(work_dir, ignore_errors=True)
    errors = sum(len(r.get('texture_errors', [])) + ('error' in r) for r in report.values())
    log('finished in %.0fs, data is %.2f GB, %d problems (see %s)' % (time.time() - t0, tree_size(out_data) / 2 ** 30, errors, rp))
    sys.exit(0 if ok and errors == 0 else 1)


if __name__ == '__main__':
    main()
