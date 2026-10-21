"""Build the final PEAK.ipa from the CI shell IPA plus the converted game data.

  python tools/package_ipa.py [--work C:/Users/v0id/peak-work] [--shell path/to/PEAK-shell.ipa] [--out name.ipa]

Without --shell the `shell-latest` release of the PRIVATE repo is downloaded with the GitHub CLI.
The IPA is written entry by entry: the shell's files are copied as they are (compressed bytes, no recompression)
except the game files it only has as placeholders (its own globalgamemanagers / level0 / sharedassets /
unity_builtin_extra), then every converted zip of convert_data.py (<work>/conv/*.zip) is copied into the app's
Data folder the same way. RuntimeInitializeOnLoads.json is the union of the shell's (PeakIOS hooks) and the
retail game's, minus assemblies that are not in the build. Nothing is unpacked to the disk.

The IPA is derived from the game: never upload it anywhere public (releases of the PRIVATE repo only).
"""
import argparse, json, os, shutil, struct, subprocess, sys, time, zipfile

PRIVATE_REPO = 'm7830380-cyber/peak-ios-private'
SHELL_ASSET = 'PEAK-shell.ipa'
GAME_DATA = r'C:\Program Files (x86)\Steam\steamapps\common\PEAK\PEAK_Data'
# shell Data files replaced by the game's (converted) ones
SHELL_GAME_FILES = ('level', 'sharedassets', 'resources.assets', 'resources.resource', 'globalgamemanagers',
                    'Resources/unity_builtin_extra')


def copy_raw(src, info, dst, name):
    """Copies one entry's stored bytes from zip `src` into zip `dst` under `name` (no recompression)."""
    src.fp.seek(info.header_offset)
    h = src.fp.read(30)
    if h[:4] != b'PK\x03\x04':
        raise ValueError('bad local header for ' + info.filename)
    n, e = struct.unpack('<HH', h[26:30])
    src.fp.seek(info.header_offset + 30 + n + e)
    zi = zipfile.ZipInfo(name, date_time=info.date_time)
    zi.compress_type = info.compress_type
    zi.CRC, zi.compress_size, zi.file_size = info.CRC, info.compress_size, info.file_size
    zi.external_attr, zi.create_system = info.external_attr, info.create_system
    zi.flag_bits = info.flag_bits & ~0x08            # sizes are in the header, no data descriptor
    zi.header_offset = dst.fp.tell()
    zip64 = zi.file_size > zipfile.ZIP64_LIMIT or zi.compress_size > zipfile.ZIP64_LIMIT
    dst.fp.write(zi.FileHeader(zip64))
    left = info.compress_size
    while left:
        chunk = src.fp.read(min(left, 1 << 22))
        if not chunk:
            raise IOError('truncated entry ' + info.filename)
        dst.fp.write(chunk)
        left -= len(chunk)
    dst.filelist.append(zi)
    dst.NameToInfo[zi.filename] = zi
    dst.start_dir = dst.fp.tell()
    dst._didModify = True


def merge_rtiol(shell_json, scripting_json):
    """Union of RuntimeInitializeOnLoads.json from the shell (PeakIOS hooks) and the retail game, keeping only
    assemblies the shell build contains (Unity Gaming Services, GDK, ... are left out of the iOS build)."""
    shell = json.loads(shell_json) if shell_json else {'root': []}
    names = set(json.loads(scripting_json)['names']) if scripting_json else None
    retail = json.load(open(os.path.join(GAME_DATA, 'RuntimeInitializeOnLoads.json')))
    key = lambda e: (e['assemblyName'], e['nameSpace'], e['className'], e['methodName'])
    seen = {key(e) for e in shell['root']}
    added = skipped = 0
    for e in retail['root']:
        if key(e) in seen:
            continue
        if names is not None and e['assemblyName'] + '.dll' not in names:
            skipped += 1
            continue
        shell['root'].append(e)
        seen.add(key(e))
        added += 1
    mods = [e['methodName'] for e in shell['root'] if e['assemblyName'] == 'PeakIOS']
    print(f'RuntimeInitializeOnLoads: +{added} retail entries ({skipped} for assemblies not in the build), PeakIOS hooks: {mods}')
    if not mods:
        raise SystemExit('the shell has no PeakIOS init hooks - mod missing from the shell build?')
    return json.dumps(shell)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--work', default=r'C:\Users\v0id\peak-work')
    ap.add_argument('--shell')
    ap.add_argument('--out', default='PEAK.ipa')
    a = ap.parse_args()

    conv = os.path.join(a.work, 'conv')
    zips = sorted(f for f in os.listdir(conv) if f.endswith('.zip')) if os.path.isdir(conv) else []
    if 'globalgamemanagers.zip' not in zips or '_streams.zip' not in zips:
        sys.exit('no converted data in %s - run tools/convert_data.py first' % conv)
    shell = a.shell
    if not shell:
        shell = os.path.join(a.work, SHELL_ASSET)
        if os.path.exists(shell):
            os.remove(shell)
        subprocess.check_call(['gh', 'release', 'download', 'shell-latest', '--repo', PRIVATE_REPO, '-p', SHELL_ASSET, '-D', a.work])

    out_dir = os.path.join(a.work, 'deliver')
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, a.out)
    t0 = time.time()
    with zipfile.ZipFile(shell) as sz, zipfile.ZipFile(out + '.tmp', 'w', zipfile.ZIP_DEFLATED, allowZip64=True) as oz:
        apps = sorted({i.filename.split('/')[1] for i in sz.infolist() if i.filename.startswith('Payload/') and i.filename.count('/') >= 2 and i.filename.split('/')[1].endswith('.app')})
        if len(apps) != 1:
            sys.exit('expected one .app in the shell, found %r' % apps)
        data = 'Payload/%s/Data/' % apps[0]
        print('shell app:', apps[0])
        rtiol = sz.read(data + 'RuntimeInitializeOnLoads.json').decode() if data + 'RuntimeInitializeOnLoads.json' in sz.NameToInfo else None
        scripting = sz.read(data + 'ScriptingAssemblies.json').decode() if data + 'ScriptingAssemblies.json' in sz.NameToInfo else None
        provided = set()               # Data-relative names the converted zips bring (they win over the shell's)
        for f in zips:
            with zipfile.ZipFile(os.path.join(conv, f)) as cz:
                provided.update(cz.namelist())
        dropped = 0
        for info in sz.infolist():
            rel = info.filename[len(data):] if info.filename.startswith(data) else None
            if rel is not None and (rel.startswith(SHELL_GAME_FILES) or rel == 'RuntimeInitializeOnLoads.json' or rel in provided):
                dropped += 1
                continue
            copy_raw(sz, info, oz, info.filename)
        oz.writestr(zipfile.ZipInfo(data + 'RuntimeInitializeOnLoads.json', date_time=time.localtime()[:6]), merge_rtiol(rtiol, scripting))
        print(f'shell: {len(sz.infolist()) - dropped} files copied, {dropped} placeholder game files left out')
        n = 0
        for f in zips:
            with zipfile.ZipFile(os.path.join(conv, f)) as cz:
                for info in cz.infolist():
                    copy_raw(cz, info, oz, data + info.filename)
                    n += 1
        print(f'{n} converted data files from {len(zips)} zips')
    os.replace(out + '.tmp', out)
    print('wrote %s: %.2f GB in %.0fs' % (out, os.path.getsize(out) / 2 ** 30, time.time() - t0))


if __name__ == '__main__':
    main()
