# Minimal Windows minidump reader (no packages): which thread crashed, its registers, thread names, and the modules
# whose code addresses sit on the crashing thread's stack (a poor man's stack trace).
# python mdread.py <file.dmp> [stack bytes to scan]
import struct, sys

path = sys.argv[1]
scan = int(sys.argv[2], 0) if len(sys.argv) > 2 else 0x4000
f = open(path, 'rb')
data = f.read()

def u32(o): return struct.unpack_from('<I', data, o)[0]
def u64(o): return struct.unpack_from('<Q', data, o)[0]
def mdstring(rva):
    n = u32(rva)
    return data[rva + 4: rva + 4 + n].decode('utf-16-le', 'replace')

assert data[:4] == b'MDMP'
count, dirrva = u32(8), u32(12)
streams = {}
for i in range(count):
    t, size, rva = struct.unpack_from('<III', data, dirrva + i * 12)
    streams.setdefault(t, (size, rva))

# modules
modules = []
size, rva = streams[4]
for i in range(u32(rva)):
    m = rva + 4 + i * 108
    base, sz = u64(m), u32(m + 8)
    modules.append((base, base + sz, mdstring(u32(m + 20)).split('\\')[-1]))
modules.sort()
def where(addr):
    for lo, hi, name in modules:
        if lo <= addr < hi: return f'{name}+0x{addr - lo:x}'
    return None

# memory (full dumps use Memory64ListStream)
ranges = []
if 9 in streams:
    size, rva = streams[9]
    n, base = u64(rva), u64(rva + 8)
    off = base
    for i in range(n):
        start, length = struct.unpack_from('<QQ', data, rva + 16 + i * 16)
        ranges.append((start, length, off))
        off += length
if 5 in streams:
    size, rva = streams[5]
    for i in range(u32(rva)):
        d = rva + 4 + i * 16
        start, length, mrva = u64(d), u32(d + 8), u32(d + 12)
        ranges.append((start, length, mrva))
def read(addr, n):
    for start, length, off in ranges:
        if start <= addr and addr + n <= start + length:
            return data[off + addr - start: off + addr - start + n]
    return None

names = {}
if 24 in streams:
    size, rva = streams[24]
    for i in range(u32(rva)):
        e = rva + 4 + i * 12
        tid, nrva = u32(e), u64(e + 4)
        names[tid] = mdstring(nrva)

threads = {}
size, rva = streams[3]
for i in range(u32(rva)):
    t = rva + 4 + i * 48
    tid = u32(t)
    ctx_size, ctx_rva = u32(t + 40), u32(t + 44)
    threads[tid] = ctx_rva

size, rva = streams[6]
tid = u32(rva)
code, addr = u32(rva + 8), u64(rva + 24)
nparams = u32(rva + 32)
params = [u64(rva + 40 + 8 * k) for k in range(nparams)]
ctx = u32(rva + 164)  # ThreadContext.Rva inside MINIDUMP_EXCEPTION_STREAM
print(f'exception 0x{code:08x} at 0x{addr:x} ({where(addr)}) params {[hex(p) for p in params]}')
print(f'thread {tid} "{names.get(tid, "")}"')
regs = ['Rax', 'Rcx', 'Rdx', 'Rbx', 'Rsp', 'Rbp', 'Rsi', 'Rdi', 'R8', 'R9', 'R10', 'R11', 'R12', 'R13', 'R14', 'R15', 'Rip']
vals = {r: u64(ctx + 0x78 + 8 * k) for k, r in enumerate(regs)}
print('  ' + '  '.join(f'{r}={v:x}' for r, v in vals.items()))
for r, v in vals.items():
    w = where(v)
    if w: print(f'  {r} -> {w}')
code_bytes = read(vals['Rip'] - 16, 32)
if code_bytes: print('  code around rip:', code_bytes.hex(' '))

print('stack:')
rsp = vals['Rsp']
seen = 0
for k in range(0, scan, 8):
    b = read(rsp + k, 8)
    if b is None: break
    v = struct.unpack('<Q', b)[0]
    w = where(v)
    if w and not w.endswith('.exe+0x0'):
        print(f'  rsp+0x{k:x}: {w}')
        seen += 1
        if seen > 60: break

print('threads:', ', '.join(f'{t}:{names.get(t, "")}' for t in sorted(threads) if names.get(t)))
