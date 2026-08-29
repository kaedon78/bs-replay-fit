import struct, sys, statistics

class R:
    def __init__(s, b): s.b = b; s.i = 0
    def u(s, f, n): v = struct.unpack_from(f, s.b, s.i); s.i += n; return v
    def i32(s): return s.u("<i", 4)[0]
    def i64(s): return s.u("<q", 8)[0]
    def f32(s): return s.u("<f", 4)[0]
    def s7(s):
        n = 0; shift = 0
        while True:
            byte = s.b[s.i]; s.i += 1
            n |= (byte & 0x7F) << shift
            if not (byte & 0x80): break
            shift += 7
        v = s.b[s.i:s.i+n].decode("utf-8"); s.i += n; return v

r = R(open(sys.argv[1], "rb").read())
magic, version, count = r.i32(), r.i32(), r.i32()
rows = []
for _ in range(count):
    f = r.s7(); written = r.i64(); played = r.i64(); song = r.s7()
    hands = []
    for _h in range(2):
        speed = r.f32(); resid = r.f32(); n = r.i32()
        r.i += n * 24            # 6 fields x 4 bytes
        hands.append((speed, resid, n))
    rows.append((f, song, hands))

print("cache: %d runs" % len(rows))
for name, idx in (("left", 0), ("right", 1)):
    sp = [h[idx][0] for _, _, h in rows]
    rs = [h[idx][1] * 1000 for _, _, h in rows]   # mm
    cu = [h[idx][2] for _, _, h in rows]
    q = lambda v, p: sorted(v)[int(p * (len(v) - 1))]
    print("\n%s hand" % name)
    print("  note speed  m/s : min %7.2f  p50 %7.2f  p95 %7.2f  max %7.2f  (negative: %d)"
          % (min(sp), q(sp,.5), q(sp,.95), max(sp), sum(1 for v in sp if v < 0)))
    print("  median resid mm : p50 %7.2f  p90 %7.2f  p95 %7.2f  p99 %7.2f  max %7.2f"
          % (q(rs,.5), q(rs,.9), q(rs,.95), q(rs,.99), max(rs)))
    print("  cuts per run    : min %4d  p50 %4d  max %4d" % (min(cu), q(cu,.5), max(cu)))
    for t in (5, 10, 20, 50):
        n = sum(1 for v in rs if v > t)
        print("     runs with median residual > %2d mm: %3d  (%4.1f%%)" % (t, n, 100*n/len(rs)))
