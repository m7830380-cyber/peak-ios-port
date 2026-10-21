"""DX11 (DXBC) Unity subprogram -> Metal subprogram conversion helpers."""
import struct
from ukblob import *

# ---------------- params merge ----------------
def merge_params(common, names, bp):
    """common: typetree m_CommonParameters, names: idx->name, bp: blob params dict (or None)."""
    out = dict(cbs={}, cbbind={}, tex=[], buf=[], uav=[], samplers=[], globals=[])

    def addvar(cbname, v):
        cb = out['cbs'].setdefault(cbname, dict(size=0, vars=[]))
        if not any(x['name'] == v['name'] for x in cb['vars']):
            cb['vars'].append(v)

    if common:
        for cb in common['m_ConstantBuffers']:
            cn = names[cb['m_NameIndex']]
            c = out['cbs'].setdefault(cn, dict(size=0, vars=[]))
            c['size'] = max(c['size'], cb['m_Size'])
            for m in cb['m_MatrixParams']:
                addvar(cn, dict(name=names[m['m_NameIndex']], type=m['m_Type'], rows=m['m_RowCount'],
                                cols=m['m_RowCount'], isMatrix=1, arraySize=m['m_ArraySize'], offset=m['m_Index']))
            for m in cb['m_VectorParams']:
                addvar(cn, dict(name=names[m['m_NameIndex']], type=m['m_Type'], rows=1, cols=m['m_Dim'],
                                isMatrix=0, arraySize=m['m_ArraySize'], offset=m['m_Index']))
            if cb['m_StructParams']:
                raise NotImplementedError('struct params')
        for b in common['m_ConstantBufferBindings']:
            out['cbbind'][names[b['m_NameIndex']]] = b['m_Index']
        for t in common['m_TextureParams']:
            out['tex'].append(dict(name=names[t['m_NameIndex']], index=t['m_Index'], sampler=t['m_SamplerIndex'],
                                   ms=int(t['m_MultiSampled']), dim=t['m_Dim']))
        for b in common['m_BufferParams']:
            out['buf'].append(dict(name=names[b['m_NameIndex']], index=b['m_Index'], arraySize=b['m_ArraySize']))
        for u in common['m_UAVParams']:
            out['uav'].append(dict(name=names[u['m_NameIndex']], index=u['m_Index'], orig=u['m_OriginalIndex']))
        for s in common['m_Samplers']:
            out['samplers'].append(dict(bind=s['bindPoint'], sampler=s['sampler']))
        if common['m_VectorParams'] or common['m_MatrixParams']:
            raise NotImplementedError('non-cb globals')
    if bp:
        for cb in bp['cbs']:
            if cb['name'] == '':
                if cb['params']:
                    raise NotImplementedError('non-cb globals')
                continue
            c = out['cbs'].setdefault(cb['name'], dict(size=0, vars=[]))
            c['size'] = max(c['size'], cb['size'])
            for p in cb['params']:
                addvar(cb['name'], dict(name=p['name'], type=p['type'], rows=p['rows'], cols=p['cols'],
                                        isMatrix=p['isMatrix'], arraySize=p['arraySize'], offset=p['index']))
            for st in cb['structs']:
                # Described to HLSLcc as a flat float4 array covering the struct array (DXBC addresses
                # cbuffers by register anyway); build_metal_params maps it back to the struct.
                if st['size'] % 16:
                    raise NotImplementedError('struct size %d not a multiple of 16' % st['size'])
                addvar(cb['name'], dict(name=st['name'], type=0, rows=1, cols=4, isMatrix=0,
                                        arraySize=st['size'] * max(1, st['arraySize']) // 16, offset=st['index'],
                                        struct=st))
        for b in bp['binds']:
            k = b['kind']
            if k == 'tex':
                out['tex'].append(dict(name=b['name'], index=b['index'], sampler=b['sampler'], ms=b['extra'] & 1, dim=b['extra'] >> 1))
            elif k == 'cb':
                out['cbbind'][b['name']] = b['index']
            elif k == 'buf':
                out['buf'].append(dict(name=b['name'], index=b['index'], arraySize=b['arraySize']))
            elif k == 'uav':
                out['uav'].append(dict(name=b['name'], index=b['index'], orig=b['orig']))
            elif k == 'sampler':
                out['samplers'].append(dict(bind=b['bind'], sampler=b['sampler']))
    return out


# ---------------- RDEF ----------------
D3D_SVT = {0: 3, 1: 2, 2: 1, 3: 3, 4: 2, 5: 19}   # unity ShaderParamType -> D3D_SHADER_VARIABLE_TYPE
SRV_DIM = {2: 4, 3: 8, 4: 9, 5: 5, 6: 10}         # unity TextureDimension -> D3D_SRV_DIMENSION


def var_size(v):
    n = max(1, v['arraySize'])
    if v['isMatrix']:
        return 16 * v['cols'] * (n - 1) + 16 * (v['cols'] - 1) + 4 * v['rows']
    return 16 * (n - 1) + 4 * v['cols']


def sampler_name_for_slot(P, slot):
    for t in P['tex']:
        if t['sampler'] == slot:
            return 'sampler' + t['name']
    return None


def build_rdef(P, sm_major, sm_minor, prog_tok, samplers_used=(), textures_used=(), decls=None):
    decls = decls or {}
    strings = bytearray()
    stroff = {}
    is5 = sm_major >= 5
    hdr_words = 6 + (8 if is5 else 0)
    cbnames = [cn for cn in P['cbs'].keys() if cn in P['cbbind']]
    res = []  # (name,type,ret,dim,samples,bind,count,flags)
    for cn in cbnames:
        res.append((cn, 0, 0, 0, 0, P['cbbind'][cn], 1, 0))
    have_samp = set()
    for s in P['samplers']:
        res.append(('sampler_inline%d' % s['bind'], 3, 0, 0, 0, s['bind'], 1, 0))
        have_samp.add(s['bind'])
    for t in P['tex']:
        sl = t['sampler']
        if 0 <= sl < 16 and sl not in have_samp:
            res.append(('sampler' + t['name'], 3, 0, 0, 0, sl, 1, 0))
            have_samp.add(sl)
    for sl in samplers_used:
        if sl not in have_samp:
            res.append(('sampler_unknown%d' % sl, 3, 0, 0, 0, sl, 1, 0))
            have_samp.add(sl)
    for t in P['tex']:
        dim = SRV_DIM.get(t['dim'], 4)
        if t['ms'] and dim == 4:
            dim = 6
        d = decls.get(('t', t['index']))
        if d and d[0] == 2:
            dim = d[1]
        res.append((t['name'], 2, d[2] if d and d[0] == 2 else 5, dim, 0 if t['ms'] else 0xFFFFFFFF, t['index'], 1, 12))
    for b in P['buf']:
        d = decls.get(('t', b['index']))
        if d:
            res.append((b['name'], d[0], d[2], d[1], 0 if d[0] != 2 else 0xFFFFFFFF, b['index'], 1, 12 if d[0] == 2 else 0))
        else:
            res.append((b['name'], 5, 6, 1, 0, b['index'], 1, 0))
    for u in P['uav']:
        d = decls.get(('u', u['index']))
        if d:
            res.append((u['name'], d[0], d[2], d[1], 0 if d[0] != 4 else 0xFFFFFFFF, u['index'], 1, 12 if d[0] == 4 else 0))
        else:
            res.append((u['name'], 6, 6, 1, 0, u['index'], 1, 0))
    # constant buffer descriptions: the real cbuffers, then one D3D_CT_RESOURCE_BIND_INFO entry per structured
    # buffer (named like the buffer, size = element stride) - HLSLcc takes a structured buffer's stride from it.
    cbdefs = [(cn, (P['cbs'][cn]['size'] + 15) // 16 * 16, sorted(P['cbs'][cn]['vars'], key=lambda v: v['offset']), 0)
              for cn in cbnames]
    for r in res:
        if r[1] in (5, 6, 9, 10, 11):
            d = decls.get(('u' if r[1] != 5 else 't', r[5]))
            stride = d[3] if d and len(d) > 3 else 4
            cbdefs.append((r[0], stride, [dict(name='$Element', offset=0, type=None, rows=1, cols=1, isMatrix=0,
                                               arraySize=stride // 4, size=stride, d3dtype=19)], 3))
    rb_words = 10 if (sm_major > 5 or (sm_major == 5 and sm_minor >= 1)) else 8
    off_res = hdr_words * 4
    off_cb = off_res + len(res) * rb_words * 4
    off_vars = off_cb + len(cbdefs) * 6 * 4
    var_words = 10 if is5 else 6
    allvars = [(cd[0], v) for cd in cbdefs for v in cd[2]]
    off_types = off_vars + len(allvars) * var_words * 4
    type_size = 36 if is5 else 16
    off_str = off_types + len(allvars) * type_size

    def S(s):
        if s not in stroff:
            stroff[s] = off_str + len(strings)
            strings.extend(s.encode() + b'\0')
        return stroff[s]

    out = bytearray()

    def w(*a):
        out.extend(struct.pack('<%dI' % len(a), *[x & 0xffffffff for x in a]))

    w(len(cbdefs), off_cb if cbdefs else 0, len(res), off_res if res else 0, prog_tok, 0x100)
    if is5:
        w(0x31314452, 60, 24, 32, 40, 36, 12, 0)
    for (n, t, ret, dim, samp, bind, cnt, fl) in res:
        w(S(n), t, ret, dim, samp, bind, cnt, fl)
        if rb_words == 10:
            w(0, 0)
    vi = 0
    for cn, size, vars_, cbtype in cbdefs:
        nv = len(vars_)
        w(S(cn), nv, off_vars + vi * var_words * 4 if nv else 0, size, 0, cbtype)
        vi += nv
    for i, (cn, v) in enumerate(allvars):
        w(S(v['name']), v['offset'], v.get('size') or var_size(v), 2, off_types + i * type_size, 0)
        if is5:
            w(0xFFFFFFFF, 0, 0xFFFFFFFF, 0)
    for i, (cn, v) in enumerate(allvars):
        if v['isMatrix']:
            cls, rows, cols = 3, v['rows'], v['cols']
        elif v['cols'] == 1:
            cls, rows, cols = 0, 1, 1
        else:
            cls, rows, cols = 1, 1, v['cols']
        t = v.get('d3dtype') or D3D_SVT.get(v['type'], 3)
        out.extend(struct.pack('<6H', cls, t, rows, cols, v['arraySize'], 0))
        w(0)
        if is5:
            w(0, 0, 0, 0, 0)
    out.extend(strings)
    while len(out) % 4:
        out.append(0)
    return bytes(out)


def dxbc_chunks(dx):
    n = struct.unpack_from('<I', dx, 28)[0]
    res = []
    for o in struct.unpack_from('<%dI' % n, dx, 32):
        fcc, ln = struct.unpack_from('<4sI', dx, o)
        res.append((fcc, dx[o + 8:o + 8 + ln]))
    return res


def build_dxbc(chunks):
    hdr = 32 + 4 * len(chunks)
    body = bytearray()
    offs = []
    for fcc, data in chunks:
        offs.append(hdr + len(body))
        body += fcc + struct.pack('<I', len(data)) + data
    total = hdr + len(body)
    return b'DXBC' + b'\0' * 16 + struct.pack('<III', 1, total, len(chunks)) + struct.pack('<%dI' % len(chunks), *offs) + bytes(body)


def shader_tokens(dx):
    for fcc, data in dxbc_chunks(dx):
        if fcc in (b'SHDR', b'SHEX'):
            return data
    raise ValueError('no shader chunk')


def dxbc_version(dx):
    tok = struct.unpack_from('<I', shader_tokens(dx), 0)[0]
    return (tok >> 4) & 0xf, tok & 0xf, tok >> 16


def declared_samplers(dx):
    """Scan SHDR for dcl_sampler (opcode 90) register indices."""
    data = shader_tokens(dx)
    toks = struct.unpack_from('<%dI' % (len(data) // 4), data, 0)
    i = 2
    res = []
    while i < len(toks):
        t = toks[i]
        op = t & 0x7ff
        if op == 0x35:  # customdata
            ln = toks[i + 1]
        else:
            ln = (t >> 24) & 0x7f
        if op == 90:  # dcl_sampler: operand token then index
            res.append(toks[i + 2])
        if ln == 0:
            break
        i += ln
    return res


# D3D10_SB_RESOURCE_DIMENSION -> D3D_SRV_DIMENSION
RESDIM_SRV = {1: 1, 2: 2, 3: 4, 4: 6, 5: 8, 6: 9, 7: 3, 8: 5, 9: 7, 10: 10, 11: 11, 12: 1}


def shex_decls(dx):
    """SRV / UAV declarations in SHDR/SHEX: {('t'|'u', register): (D3D_SHADER_INPUT_TYPE, D3D_SRV_DIMENSION, return type)}.
    Lets the synthesised RDEF describe each resource as what the code declares (typed / raw / structured)."""
    data = shader_tokens(dx)
    toks = struct.unpack_from('<%dI' % (len(data) // 4), data, 0)
    out = {}
    i = 2
    while i < len(toks):
        t = toks[i]
        op = t & 0x7ff
        ln = toks[i + 1] if op == 0x35 else (t >> 24) & 0x7f
        if op in (0x58, 0xA1, 0xA2, 0x9C, 0x9D, 0x9E):
            j = i + 1
            if t & 0x80000000:          # extended opcode token(s)
                while toks[j] & 0x80000000:
                    j += 1
                j += 1
            opnd = toks[j]
            j += 1
            if opnd & 0x80000000:               # extended operand token(s)
                while toks[j] & 0x80000000:
                    j += 1
                j += 1
            reg = toks[j]
            resdim = (t >> 11) & 0x1f
            ret = 0
            if op in (0x58, 0x9C) and j + 1 < len(toks):
                ret = toks[j + 1] & 0xf          # x component return type (1 unorm .. 5 float)
            if op == 0x58:                       # Texture*/Buffer<T>
                out[('t', reg)] = (2, RESDIM_SRV.get(resdim, 4), ret or 5)
            elif op == 0xA1:
                out[('t', reg)] = (7, 11, 6)     # ByteAddressBuffer
            elif op == 0xA2:
                out[('t', reg)] = (5, 1, 6, toks[j + 1])     # StructuredBuffer, stride
            elif op == 0x9C:                     # RWTexture*/RWBuffer<T>
                out[('u', reg)] = (4, RESDIM_SRV.get(resdim, 4), ret or 5)
            elif op == 0x9D:
                out[('u', reg)] = (8, 11, 6)     # RWByteAddressBuffer
            else:
                counter = (t >> 23) & 1
                out[('u', reg)] = (11 if counter else 6, 1, 6, toks[j + 1])   # RWStructuredBuffer (+counter), stride
        if ln == 0:
            break
        i += ln
    return out


def prep_dxbc(code, P):
    i = code.find(b'DXBC')
    dx = code[i:]
    major, minor, ptype = dxbc_version(dx)
    tok = ({0: 0xFFFF, 1: 0xFFFE, 2: 0x4753, 3: 0x4853, 4: 0x4453, 5: 0x4353}[ptype] << 16) | (major << 8) | minor
    rdef = build_rdef(P, major, minor, tok, samplers_used=declared_samplers(dx), decls=shex_decls(dx))
    chunks = [c for c in dxbc_chunks(dx) if c[0] != b'RDEF']
    return build_dxbc([(b'RDEF', rdef)] + chunks), ptype
