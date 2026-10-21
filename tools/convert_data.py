"""Convert the retail PEAK (Windows) data so the iOS player can load it.

  python tools/convert_data.py [--game "C:/Program Files (x86)/Steam/steamapps/common/PEAK"] [--work C:/Users/v0id/peak-work]
                               [--only level5,globalgamemanagers.assets] [--jobs 2] [--force]

PEAK ships its player data as one big bundle (PEAK_Data/data.unity3d, 4.5 GB unpacked) plus the audio streams
next to it (resources.resource, sharedassetsN.resource). The C: drive cannot hold the unpacked data, so every
serialized file is handled on its own: its node (and its .resS stream) is extracted from the bundle into a temp
folder, converted, and written into its own deflated zip <work>/conv/<name>.zip (entries relative to the app's
Data folder); the temp files are deleted right away. tools/package_ipa.py builds the IPA from those zips.
A file whose zip is already there (same converter version) is skipped, so a run can be resumed.

For every serialized player file (globalgamemanagers, levels, sharedassets, resources, unity_builtin_extra):
  * the target-platform tag changes from StandaloneWindows64 (19) to iOS (9);
  * every Shader gets its DX11 programs translated to Metal (convert_shader.py);
  * every ComputeShader gets its D3D11 kernels translated to Metal (convert_compute.py);
  * BCn textures become ASTC, BC6H / half-float lightmaps ASTC HDR, 4096 maps 2048 (transcode_tex.py);
  * .resS streams that hold re-encoded textures are rebuilt, all others are copied unchanged.
The audio .resource streams and StreamingAssets (Data/Raw on iOS) are stored unchanged (_streams.zip).

Everything produced here is derived from the game: never commit or upload it anywhere public.
"""
import argparse, hashlib, json, os, pickle, re, shutil, sys, time, traceback, zipfile
from concurrent.futures import ProcessPoolExecutor, as_completed

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import UnityPy
from convert_shader import convert_shader as convert_one
from convert_compute import convert_compute
from transcode_tex import transcode_file
from unityfs import UnityFS

UnityPy.config.FALLBACK_UNITY_VERSION = '6000.3.15f1'
IOS_PLATFORM = 9
DEFAULT_GAME = r'C:\Program Files (x86)\Steam\steamapps\common\PEAK'
DEFAULT_WORK = r'C:\Users\v0id\peak-work'
DATA_DIR = 'PEAK_Data'
BUNDLE = 'data.unity3d'
CONVERTER_VERSION = 'peak-1'   # bump to reconvert everything (and invalidate the shader cache)
STREAMS_ZIP = '_streams.zip'


def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)


def serialized_nodes(bundle):
    """Bundle nodes that are serialized files (not .resS / .resource streams)."""
    return [n for n in bundle.order if not n.endswith(('.resS', '.resource'))]


def zip_name(node):
    return node.replace('/', '_') + '.zip'


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


def convert_node(game, node, conv_dir, cache_dir, work_dir):
    """Extract one serialized file (+ its .resS) from the bundle, convert it, write <conv_dir>/<node>.zip."""
    t0 = time.time()
    report = {}
    bundle = UnityFS(os.path.join(game, DATA_DIR, BUNDLE))
    tmp = os.path.join(work_dir, 'node_' + node.replace('/', '_'))
    src_dir, dst_dir = os.path.join(tmp, 'src'), os.path.join(tmp, 'dst')
    shutil.rmtree(tmp, ignore_errors=True)
    os.makedirs(os.path.join(src_dir, os.path.dirname(node)), exist_ok=True)
    os.makedirs(os.path.join(dst_dir, os.path.dirname(node)), exist_ok=True)
    try:
        bundle.extract(node, os.path.join(src_dir, node))
        streams = [s for s in (node + '.resS',) if s in bundle.nodes]
        for s in streams:
            bundle.extract(s, os.path.join(src_dir, s))
        bundle.close()

        env = UnityPy.load(os.path.join(src_dir, node))
        sf = list(env.files.values())[0]
        report['shaders_converted'] = convert_shaders_in(sf, cache_dir, work_dir, report)
        report['textures_converted'] = transcode_file(sf, src_dir, dst_dir, report)
        sf._m_target_platform = IOS_PLATFORM
        with open(os.path.join(dst_dir, node), 'wb') as f:
            f.write(sf.save())
        del env, sf
        rebuilt = set(report.get('rebuilt_streams', []))

        out = os.path.join(conv_dir, zip_name(node))
        with zipfile.ZipFile(out + '.tmp', 'w', zipfile.ZIP_DEFLATED, allowZip64=True, compresslevel=6) as z:
            z.write(os.path.join(dst_dir, node), node)
            for s in streams:
                z.write(os.path.join(dst_dir if s in rebuilt else src_dir, s), s)
        os.replace(out + '.tmp', out)
        report['bytes_in'] = sum(os.path.getsize(os.path.join(src_dir, x)) for x in [node] + streams)
        report['bytes_zip'] = os.path.getsize(out)
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    report['seconds'] = round(time.time() - t0)
    return report


def job(*args):
    try:
        import convert_shader
        convert_shader.INLINE_SEEN.clear()
        rep = convert_node(*args)
        rep['inline_samplers'] = {str(k): v for k, v in convert_shader.INLINE_SEEN.items()}
        return rep
    except Exception:
        return {'error': traceback.format_exc()}


def write_streams_zip(game, conv_dir):
    """Audio streams (*.resource next to data.unity3d) and StreamingAssets (-> Data/Raw), stored as they are."""
    data = os.path.join(game, DATA_DIR)
    out = os.path.join(conv_dir, STREAMS_ZIP)
    with zipfile.ZipFile(out + '.tmp', 'w', zipfile.ZIP_STORED, allowZip64=True) as z:
        for f in sorted(os.listdir(data)):
            if f.endswith('.resource'):
                z.write(os.path.join(data, f), f)
        sa = os.path.join(data, 'StreamingAssets')
        for root, _, fs in os.walk(sa):
            for f in fs:
                p = os.path.join(root, f)
                z.write(p, 'Raw/' + os.path.relpath(p, sa).replace('\\', '/'), compress_type=zipfile.ZIP_DEFLATED)
    os.replace(out + '.tmp', out)
    return os.path.getsize(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=DEFAULT_GAME)
    ap.add_argument('--work', default=DEFAULT_WORK)
    ap.add_argument('--only', default=None, help='comma separated node names (testing)')
    ap.add_argument('--jobs', type=int, default=2, help='parallel conversions (each needs up to ~2 GB of RAM)')
    ap.add_argument('--force', action='store_true', help='reconvert files that already have a zip')
    a = ap.parse_args()

    conv_dir = os.path.join(a.work, 'conv')
    cache_dir = os.path.join(a.work, 'cache', 'shaders')
    work_dir = os.path.join(a.work, 'tmp')
    for d in (conv_dir, cache_dir, work_dir):
        os.makedirs(d, exist_ok=True)
    stamp = os.path.join(conv_dir, 'VERSION')
    if os.path.exists(stamp) and open(stamp).read().strip() != CONVERTER_VERSION:
        log('converter version changed: reconverting everything')
        for f in os.listdir(conv_dir):
            if f.endswith('.zip'):
                os.remove(os.path.join(conv_dir, f))
    open(stamp, 'w').write(CONVERTER_VERSION)

    bundle = UnityFS(os.path.join(a.game, DATA_DIR, BUNDLE))
    if bundle.unity_version != UnityPy.config.FALLBACK_UNITY_VERSION:
        log('WARNING: bundle is Unity', bundle.unity_version)
    only = set(a.only.split(',')) if a.only else None
    nodes = [n for n in serialized_nodes(bundle) if not only or n in only]
    if not a.force:
        nodes = [n for n in nodes if not os.path.exists(os.path.join(conv_dir, zip_name(n)))]
    # biggest first, so the long ones overlap with the many small ones
    sizes = {n: bundle.size(n) + (bundle.size(n + '.resS') if n + '.resS' in bundle.nodes else 0) for n in nodes}
    nodes.sort(key=lambda n: -sizes[n])
    bundle.close()

    if not only and (a.force or not os.path.exists(os.path.join(conv_dir, STREAMS_ZIP))):
        log('audio streams + StreamingAssets: %d MB' % (write_streams_zip(a.game, conv_dir) >> 20))

    log('converting %d serialized files (%.2f GB unpacked) with %d jobs' % (len(nodes), sum(sizes.values()) / 2 ** 30, a.jobs))
    rp = os.path.join(a.work, 'convert_report.json')
    report = json.load(open(rp)) if os.path.exists(rp) else {}
    t0 = time.time()
    ok = True
    with ProcessPoolExecutor(max_workers=a.jobs) as ex:
        futs = {ex.submit(job, a.game, n, conv_dir, cache_dir, work_dir): n for n in nodes}
        for fu in as_completed(futs):
            n = futs[fu]
            rep = fu.result()
            report[n] = rep
            if rep.get('error'):
                ok = False
                log('FAILED', n, rep['error'])
            else:
                sh = dict(rep.get('shaders', {}), **rep.get('compute_shaders', {}))
                bad = sorted(k for k, s in sh.items() if s.get('fail'))
                log('done %-30s %4ds  %4d MB -> %4d MB zip  shaders %3d (with failures: %d)  textures %4d (errors %d)' % (
                    n, rep['seconds'], rep['bytes_in'] >> 20, rep['bytes_zip'] >> 20, rep['shaders_converted'], len(bad),
                    rep['textures_converted'], len(rep.get('texture_errors', []))))
            json.dump(report, open(rp, 'w'), indent=1)
    shutil.rmtree(work_dir, ignore_errors=True)
    total = sum(os.path.getsize(os.path.join(conv_dir, f)) for f in os.listdir(conv_dir) if f.endswith('.zip'))
    errors = sum(len(r.get('texture_errors', [])) + ('error' in r) for r in report.values())
    log('finished in %.0fs, converted zips total %.2f GB, %d problems (see %s)' % (time.time() - t0, total / 2 ** 30, errors, rp))
    sys.exit(0 if ok and errors == 0 else 1)


if __name__ == '__main__':
    main()
