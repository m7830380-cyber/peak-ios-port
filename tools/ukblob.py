"""Reader/writer for Unity 2022.3 shader blobs (decompressed per-platform blob)."""
import struct, io

class R:
    def __init__(s, b): s.b=b; s.p=0
    def i(s): v=struct.unpack_from('<i',s.b,s.p)[0]; s.p+=4; return v
    def u(s): v=struct.unpack_from('<I',s.b,s.p)[0]; s.p+=4; return v
    def align(s): s.p=(s.p+3)&~3
    def str(s):
        n=s.i(); v=s.b[s.p:s.p+n].decode('utf8','replace'); s.p+=n; s.align(); return v
    def bytes(s,n): v=s.b[s.p:s.p+n]; s.p+=n; return v
class W:
    def __init__(s): s.o=io.BytesIO()
    def i(s,v): s.o.write(struct.pack('<i',v))
    def u(s,v): s.o.write(struct.pack('<I',v&0xffffffff))
    def align(s):
        while s.o.tell()%4: s.o.write(b'\0')
    def str(s,v):
        b=v.encode('utf8'); s.i(len(b)); s.o.write(b); s.align()
    def raw(s,b): s.o.write(b)
    def get(s): return s.o.getvalue()

def read_entries(blob):
    n=struct.unpack_from('<i',blob,0)[0]
    return [struct.unpack_from('<iii',blob,4+12*k) for k in range(n)]

def split_blob(blob):
    return [blob[o:o+l] for (o,l,s) in read_entries(blob)]

def join_blob(segs):
    hdr=4+12*len(segs); out=bytearray(struct.pack('<i',len(segs))); body=bytearray(); offs=[]
    for s in segs:
        offs.append((hdr+len(body),len(s))); body+=s
    for o,l in offs: out+=struct.pack('<iii',o,l,0)
    return bytes(out+body)

# ---------- params ----------
def read_cbparam(r):
    return dict(name=r.str(), type=r.i(), rows=r.i(), cols=r.i(), isMatrix=r.i(), arraySize=r.i(), index=r.i())
def write_cbparam(w,p):
    w.str(p['name']); w.i(p['type']); w.i(p['rows']); w.i(p['cols']); w.i(p['isMatrix']); w.i(p['arraySize']); w.i(p['index'])
def read_cb(r):
    cb=dict(name=r.str(), size=r.i())
    cb['params']=[read_cbparam(r) for _ in range(r.i())]
    structs=[]
    for _ in range(r.i()):
        st=dict(name=r.str(), index=r.i(), arraySize=r.i(), size=r.i())
        st['params']=[read_cbparam(r) for _ in range(r.i())]
        structs.append(st)
    cb['structs']=structs
    return cb
def write_cb(w,cb):
    w.str(cb['name']); w.i(cb['size']); w.i(len(cb['params']))
    for p in cb['params']: write_cbparam(w,p)
    w.i(len(cb['structs']))
    for st in cb['structs']:
        w.str(st['name']); w.i(st['index']); w.i(st['arraySize']); w.i(st['size']); w.i(len(st['params']))
        for p in st['params']: write_cbparam(w,p)

def read_params(seg):
    r=R(seg); P={'version':r.i()}
    P['cbs']=[read_cb(r) for _ in range(r.i())]
    binds=[]
    for _ in range(r.i()):
        name=r.str(); t=r.i()
        if t==0: b=dict(kind='tex',name=name,index=r.i(),sampler=r.i(),extra=r.u())
        elif t in (1,2): b=dict(kind={1:'cb',2:'buf'}[t],name=name,index=r.i(),arraySize=r.i())
        elif t==3: b=dict(kind='uav',name=name,index=r.i(),orig=r.i())
        elif t==4: b=dict(kind='sampler',name=name,bind=r.i(),sampler=r.u())
        else: raise ValueError('bind type %d'%t)
        binds.append(b)
    P['binds']=binds
    P['tail']=seg[r.p:]
    return P
def write_params(P):
    w=W(); w.i(P['version']); w.i(len(P['cbs']))
    for cb in P['cbs']: write_cb(w,cb)
    w.i(len(P['binds']))
    for b in P['binds']:
        w.str(b['name'])
        k=b['kind']
        if k=='tex': w.i(0); w.i(b['index']); w.i(b['sampler']); w.u(b['extra'])
        elif k in('cb','buf'): w.i(1 if k=='cb' else 2); w.i(b['index']); w.i(b['arraySize'])
        elif k=='uav': w.i(3); w.i(b['index']); w.i(b['orig'])
        elif k=='sampler': w.i(4); w.i(b['bind']); w.u(b['sampler'])
    w.raw(P.get('tail',b''))
    return w.get()

# ---------- subprogram ----------
def read_sub(seg):
    r=R(seg); S={'version':r.i(),'type':r.i(),'stats':[r.i() for _ in range(4)]}
    S['keywords']=[r.str() for _ in range(r.i())]
    n=r.i(); S['code']=r.bytes(n); r.align()
    S['sourceMap']=r.i(); S['channels']=[(r.i(),r.i()) for _ in range(r.i())]
    S['tail']=seg[r.p:]
    return S
def write_sub(S):
    w=W(); w.i(S['version']); w.i(S['type'])
    for x in S['stats']: w.i(x)
    w.i(len(S['keywords']))
    for k in S['keywords']: w.str(k)
    w.i(len(S['code'])); w.raw(S['code']); w.align()
    w.i(S['sourceMap']); w.i(len(S['channels']))
    for a,b in S['channels']: w.i(a); w.i(b)
    w.raw(S.get('tail',b''))
    return w.get()

def is_params(seg):
    return struct.unpack_from('<i',seg,4)[0] < 4 and False
