"""Reader/writer for Unity 2020.3 shader subprogram blob segments.

Layout of one (decompressed) platform blob: int count, then count x (offset, length, chunk) and the segments.
Each segment is one subprogram:
  int version (201806140), int gpuProgramType, int stats[4], string[] globalKeywords, string[] localKeywords,
  byte[] code (aligned), int sourceMap, (int source, int target)[] channels,
  then the parameters (2022-style, without a version int): cb[] constantBuffers, bind[] bindings.
"""
import struct
from ukblob import R, W, read_cb, write_cb

SUB_VERSION = 201806140


def read_binds(r):
    binds = []
    for _ in range(r.i()):
        name = r.str(); t = r.i()
        if t == 0: b = dict(kind='tex', name=name, index=r.i(), sampler=r.i(), extra=r.u())
        elif t in (1, 2): b = dict(kind={1: 'cb', 2: 'buf'}[t], name=name, index=r.i(), arraySize=r.i())
        elif t == 3: b = dict(kind='uav', name=name, index=r.i(), orig=r.i())
        elif t == 4: b = dict(kind='sampler', name=name, bind=r.i(), sampler=r.u())
        else: raise ValueError('bind type %d' % t)
        binds.append(b)
    return binds


def write_binds(w, binds):
    w.i(len(binds))
    for b in binds:
        w.str(b['name'])
        k = b['kind']
        if k == 'tex': w.i(0); w.i(b['index']); w.i(b['sampler']); w.u(b['extra'])
        elif k in ('cb', 'buf'): w.i(1 if k == 'cb' else 2); w.i(b['index']); w.i(b['arraySize'])
        elif k == 'uav': w.i(3); w.i(b['index']); w.i(b['orig'])
        elif k == 'sampler': w.i(4); w.i(b['bind']); w.u(b['sampler'])


def read_sub(seg):
    r = R(seg)
    S = {'version': r.i(), 'type': r.i(), 'stats': [r.i() for _ in range(4)]}
    S['globalKw'] = [r.str() for _ in range(r.i())]
    S['localKw'] = [r.str() for _ in range(r.i())]
    n = r.i(); S['code'] = r.bytes(n); r.align()
    S['sourceMap'] = r.i()
    S['channels'] = [(r.i(), r.i()) for _ in range(r.i())]
    S['cbs'] = [read_cb(r) for _ in range(r.i())]
    S['binds'] = read_binds(r)
    S['tail'] = seg[r.p:]
    return S


def write_sub(S):
    w = W(); w.i(S['version']); w.i(S['type'])
    for x in S['stats']: w.i(x)
    for kws in (S['globalKw'], S['localKw']):
        w.i(len(kws))
        for k in kws: w.str(k)
    w.i(len(S['code'])); w.raw(S['code']); w.align()
    w.i(S['sourceMap']); w.i(len(S['channels']))
    for a, b in S['channels']: w.i(a); w.i(b)
    w.i(len(S['cbs']))
    for cb in S['cbs']: write_cb(w, cb)
    write_binds(w, S['binds'])
    w.raw(S.get('tail', b''))
    return w.get()
