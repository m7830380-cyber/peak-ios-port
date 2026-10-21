"""Convert a Unity 6 ComputeShader typetree with D3D11 kernels to a Metal-only one.

ComputeShader layout: variants[] (one per graphics API: targetRenderer 2 = D3D11, 16 = Metal), each with
kernels[] -> uniqueVariants[] (resource tables + code) and the variant-wide constantBuffers[] layouts.
D3D11 code is raw DXBC without RDEF; an RDEF is synthesised from the resource tables (dx2metal.prep_dxbc)
and HLSLcc translates it to MSL. Resource bind points, cbuffer layouts and the code header (0xDEADCAFE,
version 2, header size 20, threadgroup size) follow the shell's own Unity 6 Metal compute shaders.
A kernel that fails to translate is left out; Unity then reports "Kernel at index (n) is invalid" for it
instead of running garbage, and the C# side of most users (VFX, APV, GPUInstancer) checks support first.
"""
import os, re, struct, copy, hashlib
from dx2metal import prep_dxbc
from convert_shader import HlslccBatch, metal_layout, ConvertError

RENDERER_D3D11, RENDERER_METAL = 2, 16


def metal_compute_code(msl, tg):
    msl = re.sub(rb'kernel void \w+\(', b'kernel void computeMain(', msl, count=1)
    x, y, z = (list(tg) + [1, 1, 1])[:3]
    return struct.pack('<IIIII', 0xDEADCAFE, 2, 20, (x & 0xffff) | (y << 16), (z & 0xffff) | (1 << 16)) + msl


def dx_params(v, u):
    """Unity compute resource tables -> the P dict dx2metal.build_rdef understands."""
    P = dict(cbs={}, cbbind={}, tex=[], buf=[], uav=[], samplers=[], globals=[])
    for i, cb in enumerate(u['cbs']):
        d = v['constantBuffers'][u['cbVariantIndices'][i]]
        P['cbs'][cb['name']] = dict(size=d['byteSize'], vars=[
            dict(name=p['name'], type=p['type'], rows=p['rowCount'], cols=p['colCount'], isMatrix=int(p['rowCount'] > 1),
                 arraySize=p['arraySize'], offset=p['offset']) for p in d['params']])
        P['cbbind'][cb['name']] = cb['bindPoint']
    for t in u['textures']:
        P['tex'].append(dict(name=t['name'], index=t['bindPoint'], sampler=t['samplerBindPoint'], ms=0,
                             dim=max(2, t['texDimension'])))
    for b in u['inBuffers']:
        P['buf'].append(dict(name=b['name'], index=b['bindPoint'], arraySize=0))
    for b in u['outBuffers']:
        P['uav'].append(dict(name=b['name'], index=b['bindPoint'], orig=b['bindPoint'], texDim=b['texDimension']))
    if u['builtinSamplers']:
        raise ConvertError('builtin samplers not supported')
    return P


def res(name, bind, samp=-1, dim=-1):
    return dict(name=name, generatedName='', bindPoint=bind, samplerBindPoint=samp, texDimension=dim)


def metal_resources(refl, P, dxcbs):
    """HLSLcc reflection -> (cb layouts [(name, byteSize, params)], cbs, textures, inBuffers, outBuffers)."""
    layouts, cbs, tex, inb, outb = [], [], [], [], []
    cur, off = None, 0
    orig = {}
    for cn, cb in P['cbs'].items():
        for var in cb['vars']:
            orig[(cn, var['name'])] = var
    for r in refl['refl']:
        k = r['k']
        if k == 'cb':
            cur = dict(name=r['name'], byteSize=0, params=[])
            layouts.append(cur)
            off = 0
        elif k == 'const':
            size, align = metal_layout(r)
            off = (off + align - 1) // align * align
            dxname = '$Globals' if cur['name'] == 'Globals' and '$Globals' in P['cbs'] else cur['name']
            ov = orig.get((dxname, r['name']))
            if ov is None:
                raise ConvertError('unknown const %s.%s' % (dxname, r['name']))
            cur['params'].append(dict(name=r['name'], type=ov['type'], offset=off, arraySize=ov['arraySize'],
                                      rowCount=ov['rows'], colCount=ov['cols']))
            off += size
            cur['byteSize'] = off
        elif k == 'cbbind':
            cbs.append(res(r['name'], r['index']))
        elif k == 'tex':
            if r['uav']:
                ou = next((u for u in P['uav'] if u['name'] == r['name']), None)
                outb.append(res(r['name'], r['index'], dim=ou['texDim'] if ou and ou['texDim'] >= 0 else 2))
            else:
                ot = next((t for t in P['tex'] if t['name'] == r['name']), None)
                if ot is None:
                    raise ConvertError('unknown texture %s' % r['name'])
                tex.append(res(r['name'], r['index'], r['sampler'] if r['sampler'] >= 0 else -1, ot['dim']))
        elif k == 'buf':
            (outb if r['uav'] else inb).append(res(r['name'], r['index']))
    return layouts, cbs, tex, inb, outb


def convert_compute(tt, workdir, log=print):
    """Returns (new typetree, stats) or None when there are no D3D11 kernels."""
    dxv = [v for v in tt['variants'] if v['targetRenderer'] == RENDERER_D3D11]
    if not dxv:
        return None
    v = min(dxv, key=lambda x: x['targetLevel'])
    name = tt['m_Name']
    batch = HlslccBatch(workdir)
    plan = {}
    stats = dict(ok=0, fail=0)
    for ki, k in enumerate(v['kernels']):
        for ui, u in enumerate(k['uniqueVariants']):
            key = 'c%d_%d' % (ki, ui)
            try:
                P = dx_params(v, u)
                dx, _ = prep_dxbc(bytes(u['code']), P)
                batch.add(key, dx)
                plan[(ki, ui)] = (key, P)
            except Exception as ex:
                plan[(ki, ui)] = None
                log('  [%s/%s] prep failed: %r' % (name, k['name'], ex))
    batch.run()

    nv = copy.deepcopy(v)
    nv['targetRenderer'] = RENDERER_METAL
    nv['targetLevel'] = 0
    layouts = []            # Metal constantBuffers of the variant
    layout_idx = {}
    for ki, k in enumerate(nv['kernels']):
        nuv = []
        remap = {}
        for ui, u in enumerate(k['uniqueVariants']):
            item = plan.get((ki, ui))
            msl, refl = batch.result(item[0]) if item else (None, None)
            if msl is None:
                stats['fail'] += 1
                if item:
                    log('  [%s/%s] hlslcc failed%s' % (name, k['name'], (': ' + str([d['msg'] for d in refl['refl'] if d['k'] == 'diag'])[:300]) if refl else ' (crash)'))
                continue
            try:
                lay, cbs, tex, inb, outb = metal_resources(refl, item[1], v['constantBuffers'])
            except Exception as ex:
                stats['fail'] += 1
                log('  [%s/%s] resources failed: %r' % (name, k['name'], ex))
                continue
            cbidx = []
            for cb in cbs:
                l = next((x for x in lay if x['name'] == cb['name']), dict(name=cb['name'], byteSize=0, params=[]))
                lk = repr(l)
                if lk not in layout_idx:
                    layout_idx[lk] = len(layouts)
                    layouts.append(l)
                cbidx.append(layout_idx[lk])
            nu = dict(u)
            nu.update(cbVariantIndices=cbidx, cbs=cbs, textures=tex, builtinSamplers=[], inBuffers=inb, outBuffers=outb,
                      code=metal_compute_code(msl, u['threadGroupSize']))
            remap[ui] = len(nuv)
            nuv.append(nu)
            stats['ok'] += 1
        k['uniqueVariants'] = nuv
        k['variantIndices'] = [(kw, remap[i]) for kw, i in k['variantIndices'] if i in remap]
    nv['constantBuffers'] = layouts
    nt = dict(tt)
    nt['variants'] = [nv]
    log('  [%s] kernels ok=%d fail=%d' % (name, stats['ok'], stats['fail']))
    return nt, stats
