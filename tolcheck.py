exec(open("signcheck.py").read().split("import random")[0])
import random
def norm(v):
    m = math.sqrt(dot(v,v)); return tuple(p/m for p in v)
def applied_euler(typed, left, legacy=(0,0,0)):
    t = tuple(a+b for a,b in zip(legacy, typed))
    return (t[0], -t[1], -t[2]) if left else t
def applied(typed, left, root):
    return qmul(root, unity_euler(*applied_euler(typed, left)))

for left in (False, True):
    rng = random.Random(90210)
    worst = 0.0; smallest = 9e9; worst_flip = 0.0
    for _ in range(40):
        root = unity_euler(rng.uniform(-25,25), rng.uniform(-40,40), rng.uniform(-15,15))
        cur = (rng.uniform(35,52), rng.uniform(-12,4), rng.uniform(-8,8))
        cand = tuple(c + rng.uniform(-2,2) for c in cur)
        A = applied(cur, left, root); B = applied(cand, left, root)
        g = (rng.uniform(-.3,.3), rng.uniform(.8,1.3), rng.uniform(-.2,.2))
        c = add(g, qrot(A, (rng.uniform(-.06,.06), rng.uniform(-.06,.06), rng.uniform(.6,1.0))))
        ac = norm((rng.uniform(-1,1), rng.uniform(-1,1), rng.uniform(-.15,.15)))
        b1 = qrot(A,(0,0,1)); b2 = qrot(B,(0,0,1))
        L = dot(sub(c,g), b1)
        s1 = dot(sub(sub(c,g), scale(b1,L)), qrot(A,ac))
        s2 = dot(sub(sub(c,g), scale(b2,L)), qrot(B,ac))
        t = turn_vector(qmul(qinv(A), B))
        shift = L*(t[0]*ac[1] - t[1]*ac[0])
        worst = max(worst, abs(abs(s1 + shift) - abs(s2)))
        worst_flip = max(worst_flip, abs(abs(s1 - shift) - abs(s2)))
        smallest = min(smallest, abs(s2 - s1))
    print("%-5s  corrected worst %6.3f mm | flipped worst %7.3f mm | smallest shift %5.1f mm"
          % ("left" if left else "right", worst*1000, worst_flip*1000, smallest*1000))
