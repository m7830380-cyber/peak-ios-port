"""Build the final PEAK.ipa from the CI shell IPA plus the converted game data.

  python tools/package_ipa.py [--work C:/Users/v0id/peak-work] [--shell path/to/PEAK-shell.ipa] [--upload] [--serve]

Without --shell the `shell-latest` release of the PRIVATE repo is downloaded with the GitHub CLI.
The shell's own Data files (IL2CPP metadata, "unity default resources", unity_builtin_extra, ...) stay; the game's
scenes/assets/streaming assets come from convert_data.py's output (<work>/data/Data). The staging folder
hardlinks the converted files (no extra disk space) and is deleted afterwards.
--upload publishes the IPA as the `ipa-latest` release of the PRIVATE repo (it contains game code + data).
--serve starts a small web server on this PC so the IPA can be downloaded with Safari over Wi-Fi.
The IPA is derived from the game: never upload it anywhere public.
"""
import argparse, functools, http.server, json, os, shutil, socket, subprocess, sys, zipfile

PRIVATE_REPO = 'm7830380-cyber/peak-ios-private'
SHELL_ASSET = 'PEAK-shell.ipa'
GAME_DATA = r'C:\Program Files (x86)\Steam\steamapps\common\PEAK\PEAK_Data'
# shell files replaced by the game's (converted) ones
SHELL_GAME_FILES = ('level', 'sharedassets', 'resources.assets', 'resources.resource', 'globalgamemanagers')
STORE_EXT = ('.resS', '.resource', '.assets', '.bundle', '.webm')


def find_app(payload):
    for n in os.listdir(payload):
        if n.endswith('.app'):
            return os.path.join(payload, n)
    raise SystemExit('no .app in shell ipa')


def merge_rtiol(app_data):
    """Union of RuntimeInitializeOnLoads.json from the retail game and the shell (which adds PeakIOS)."""
    p_shell = os.path.join(app_data, 'RuntimeInitializeOnLoads.json')
    shell = json.load(open(p_shell)) if os.path.exists(p_shell) else {'root': []}
    retail = json.load(open(os.path.join(GAME_DATA, 'RuntimeInitializeOnLoads.json')))
    key = lambda e: (e['assemblyName'], e['nameSpace'], e['className'], e['methodName'])
    seen = {key(e) for e in shell['root']}
    added = [e for e in retail['root'] if key(e) not in seen]
    shell['root'] += added
    json.dump(shell, open(p_shell, 'w'))
    mods = [e['methodName'] for e in shell['root'] if e['assemblyName'] == 'PeakIOS']
    print(f'RuntimeInitializeOnLoads: +{len(added)} retail entries, PeakIOS hooks: {mods}')
    if not mods:
        raise SystemExit('the shell has no PeakIOS init hooks - mod missing from the shell build?')


def link_or_copy(src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    if os.path.exists(dst):
        os.remove(dst)
    try:
        os.link(src, dst)
    except OSError:
        shutil.copyfile(src, dst)


def lan_ip():
    s = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        s.connect(('10.255.255.255', 1))     # no packet is sent; picks the interface of the default route
        return s.getsockname()[0]
    finally:
        s.close()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--work', default=r'C:\Users\v0id\peak-work')
    ap.add_argument('--shell')
    ap.add_argument('--upload', action='store_true')
    ap.add_argument('--serve', action='store_true')
    ap.add_argument('--port', type=int, default=8642)
    a = ap.parse_args()

    conv = os.path.join(a.work, 'data', 'Data')
    if not os.path.exists(os.path.join(conv, 'globalgamemanagers')):
        sys.exit('no converted data in %s - run tools/convert_data.py first' % conv)
    shell = a.shell
    if not shell:
        shell = os.path.join(a.work, SHELL_ASSET)
        if os.path.exists(shell):
            os.remove(shell)
        subprocess.check_call(['gh', 'release', 'download', 'shell-latest', '--repo', PRIVATE_REPO, '-p', SHELL_ASSET, '-D', a.work])

    out_dir = os.path.join(a.work, 'deliver')
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, 'PEAK.ipa')
    stage = os.path.join(a.work, 'ipa_stage')
    shutil.rmtree(stage, ignore_errors=True)
    try:
        with zipfile.ZipFile(shell) as z:
            z.extractall(stage)
        app = find_app(os.path.join(stage, 'Payload'))
        data = os.path.join(app, 'Data')
        print('shell app:', app)
        for n in os.listdir(data):
            if n.startswith(SHELL_GAME_FILES):
                os.remove(os.path.join(data, n))
        n = 0
        for root, _, files in os.walk(conv):
            for f in files:
                src = os.path.join(root, f)
                link_or_copy(src, os.path.join(data, os.path.relpath(src, conv)))
                n += 1
        print(f'{n} converted data files')
        merge_rtiol(data)

        if os.path.exists(out):
            os.remove(out)
        with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, allowZip64=True, compresslevel=6) as z:
            for root, dirs, files in os.walk(os.path.join(stage, 'Payload')):
                for f in files:
                    p = os.path.join(root, f)
                    arc = os.path.relpath(p, stage).replace('\\', '/')
                    ct = zipfile.ZIP_STORED if f.endswith(STORE_EXT) else zipfile.ZIP_DEFLATED
                    z.write(p, arc, compress_type=ct)
        print('wrote', out, os.path.getsize(out) >> 20, 'MB')
    finally:
        shutil.rmtree(stage, ignore_errors=True)

    if a.upload:
        tag = 'ipa-latest'
        if subprocess.call(['gh', 'release', 'view', tag, '--repo', PRIVATE_REPO], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) != 0:
            subprocess.check_call(['gh', 'release', 'create', tag, '--repo', PRIVATE_REPO, '--prerelease',
                                   '--title', 'PEAK iOS (TrollStore)', '--notes', 'Install with TrollStore.'])
        subprocess.check_call(['gh', 'release', 'upload', tag, out, '--clobber', '--repo', PRIVATE_REPO])
        print('uploaded to', PRIVATE_REPO, tag)

    if a.serve:
        url = 'http://%s:%d/PEAK.ipa' % (lan_ip(), a.port)
        print('\nOn the iPhone (same Wi-Fi), open in Safari:\n  %s\nCtrl+C to stop.' % url)
        handler = functools.partial(http.server.SimpleHTTPRequestHandler, directory=out_dir)
        http.server.ThreadingHTTPServer(('', a.port), handler).serve_forever()


if __name__ == '__main__':
    main()
