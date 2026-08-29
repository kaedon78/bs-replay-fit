import math

def qmul(a, b):
    ax,ay,az,aw = a; bx,by,bz,bw = b
    return (aw*bx + ax*bw + ay*bz - az*by,
            aw*by - ax*bz + ay*bw + az*bx,
            aw*bz + ax*by - ay*bx + az*bw,
            aw*bw - ax*bx - ay*by - az*bz)

def axis_q(axis, deg):
    a = math.radians(deg) / 2.0
    s = math.sin(a)
    return (axis[0]*s, axis[1]*s, axis[2]*s, math.cos(a))

def unity_euler(x, y, z):
    # Unity: rotate z about Z, then x about X, then y about Y  ->  q = Ry * Rx * Rz
    return qmul(axis_q((0,1,0), y), qmul(axis_q((1,0,0), x), axis_q((0,0,1), z)))

def qrot(q, v):
    x,y,z,w = q; vx,vy,vz = v
    # standard q v q*
    tx = 2*(y*vz - z*vy); ty = 2*(z*vx - x*vz); tz = 2*(x*vy - y*vx)
    return (vx + w*tx + (y*tz - z*ty),
            vy + w*ty + (z*tx - x*tz),
            vz + w*tz + (x*ty - y*tx))

def qinv(q):
    x,y,z,w = q
    return (-x,-y,-z,w)

def dot(a,b): return sum(p*q for p,q in zip(a,b))
def sub(a,b): return tuple(p-q for p,q in zip(a,b))
def add(a,b): return tuple(p+q for p,q in zip(a,b))
def scale(v,s): return tuple(p*s for p in v)

# --- validate the Euler convention against a real log line -----------------
# LeftHand setting rot (44,0,4), alt handling -> applied Euler (44,0,-4)
# logged quat (0.374378, 0.013074, -0.032358, 0.926619)
q = unity_euler(44.0, 0.0, -4.0)
print("euler check: got %s" % (tuple(round(c,6) for c in q),))
print("             log (0.374378, 0.013074, -0.032358, 0.926619)")

def turn_vector(qt):
    x,y,z,w = qt
    if w < 0: x,y,z,w = -x,-y,-z,-w
    w = max(-1.0, min(1.0, w))
    ang = 2*math.acos(w)
    s = math.sqrt(max(1 - w*w, 0.0))
    if s < 1e-9 or ang < 1e-9: return (0.0, 0.0)
    k = ang/s
    return (x*k, y*k)

def applied_euler(typed, left, legacy=(0,0,0)):
    t = tuple(a+b for a,b in zip(legacy, typed))
    return (t[0], -t[1], -t[2]) if left else t

def applied(typed, left, root):
    e = applied_euler(typed, left)
    return qmul(root, unity_euler(*e))

def norm(v):
    m = math.sqrt(dot(v,v)); return tuple(p/m for p in v)

import random
random.seed(7)
print("\n%-6s %8s %10s %10s %10s" % ("hand","d_typed","actual","code","flipped"))
bad_code = bad_flip = 0
for trial in range(6):
    left  = trial % 2 == 0
    root  = unity_euler(random.uniform(-20,20), random.uniform(-30,30), random.uniform(-10,10))
    s1    = (44.0, 0.0, 4.0) if left else (49.0, -9.0, -6.0)
    dy    = random.choice([-3.0,-2.0,-1.0,1.0,2.0,3.0])
    s2    = (s1[0], s1[1]+dy, s1[2])

    A1 = applied(s1, left, root)
    A2 = applied(s2, left, root)

    g = (random.uniform(-.3,.3), random.uniform(.8,1.3), random.uniform(-.2,.2))
    c = add(g, qrot(A1, (random.uniform(-.05,.05), random.uniform(-.05,.05), random.uniform(.6,1.0))))
    across_local = norm((random.uniform(-1,1), random.uniform(-1,1), random.uniform(-.15,.15)))

    blade1 = qrot(A1, (0,0,1)); blade2 = qrot(A2, (0,0,1))
    L = dot(sub(c,g), blade1)                    # the code's Lever
    n1 = qrot(A1, across_local); n2 = qrot(A2, across_local)

    signed1 = dot(sub(sub(c,g), scale(blade1,L)), n1)
    signed2 = dot(sub(sub(c,g), scale(blade2,L)), n2)

    turn = turn_vector(qmul(qinv(A1), A2))
    shift = L * (turn[0]*across_local[1] - turn[1]*across_local[0])
    code    = signed1 - shift
    flipped = signed1 + shift
    bad_code += abs(code-signed2) > abs(flipped-signed2)
    bad_flip += abs(flipped-signed2) > abs(code-signed2)
    print("%-6s %8.1f %10.5f %10.5f %10.5f" % ("left" if left else "right", dy, signed2, code, flipped))
print("\ncode form worse in %d/6 trials; flipped form worse in %d/6" % (bad_code, bad_flip))

print("\n--- tiny angles, where the linearisation is near-exact ---")
print("%-6s %9s %12s %12s %12s" % ("hand","d_typed","actual","code","flipped"))
random.seed(11)
for trial in range(4):
    left = trial % 2 == 0
    root = unity_euler(12.0, -25.0, 3.0)
    s1 = (44.0, 0.0, 4.0) if left else (49.0, -9.0, -6.0)
    dy = 0.05 * (1 if trial % 3 else -1)
    s2 = (s1[0], s1[1]+dy, s1[2])
    A1 = applied(s1, left, root); A2 = applied(s2, left, root)
    g = (0.1, 1.0, -0.05)
    c = add(g, qrot(A1, (0.03, -0.02, 0.85)))
    across_local = norm((0.8, -0.55, 0.05))
    blade1 = qrot(A1,(0,0,1)); blade2 = qrot(A2,(0,0,1))
    L = dot(sub(c,g), blade1)
    signed1 = dot(sub(sub(c,g), scale(blade1,L)), qrot(A1, across_local))
    signed2 = dot(sub(sub(c,g), scale(blade2,L)), qrot(A2, across_local))
    turn = turn_vector(qmul(qinv(A1), A2))
    shift = L*(turn[0]*across_local[1] - turn[1]*across_local[0])
    print("%-6s %9.2f %12.8f %12.8f %12.8f" % ("left" if left else "right", dy,
          signed2, signed1-shift, signed1+shift))

print("\n=== real-data check: two settings groups from the user's own fit ===")
# From the fit log. Group A: 289 runs on L(42,-5,0). Group B: 11 runs on L(44,0,4).
# Reported turns, in degrees, in the code's own convention.
tA = (-3.00, 3.80)
tB = ( 0.62, 4.35)
for hand, left, SA, SB, tA_, tB_ in [
    ("left",  True,  (42.0,-5.0,0.0), (44.0, 0.0, 4.0), tA, tB),
]:
    A = applied((SA), left, (0,0,0,1))
    B = applied((SB), left, (0,0,0,1))
    turn = turn_vector(qmul(qinv(A), B))
    turn_deg = (math.degrees(turn[0]), math.degrees(turn[1]))
    diff = (tA_[0]-tB_[0], tA_[1]-tB_[1])
    print("  Turn(A->B)          = (%7.2f, %7.2f) deg" % turn_deg)
    print("  fitted tA - tB      = (%7.2f, %7.2f) deg" % diff)
    print("  if code sign right, tA-tB should equal +Turn = (%7.2f, %7.2f)" % turn_deg)
    print("  if code sign wrong, tA-tB should equal -Turn = (%7.2f, %7.2f)"
          % (-turn_deg[0], -turn_deg[1]))
    d_pos = math.hypot(diff[0]-turn_deg[0], diff[1]-turn_deg[1])
    d_neg = math.hypot(diff[0]+turn_deg[0], diff[1]+turn_deg[1])
    print("  distance to +Turn: %5.2f deg | to -Turn: %5.2f deg  ->  %s"
          % (d_pos, d_neg, "code sign RIGHT" if d_pos < d_neg else "code sign WRONG"))
