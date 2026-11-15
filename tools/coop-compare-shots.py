#!/usr/bin/env python3
"""Compare the host's and the guest's puppet dumps from -coop-autotest cmds (coop/shots/<name>_syncN.txt).
Usage: tools/coop-compare-shots.py [shots dir]  (default: the Windows Documents path used by coop-local-test.sh)"""
import sys, math, os
d = sys.argv[1] if len(sys.argv) > 1 else "/mnt/c/Users/DELL/Documents/Klei/OxygenNotIncluded/coop/shots"
# the authority's files: "Alice" (a player host) or "Server" (the dedicated server)
HOST = "Server" if any(f.startswith("Server_") for f in os.listdir(d)) else "Alice"
for n in range(1, 10):
    fa, fb = os.path.join(d, f"{HOST}_sync{n}.txt"), os.path.join(d, f"Bob_sync{n}.txt")
    if not (os.path.exists(fa) and os.path.exists(fb)): continue
    def load(f):
        r = {}
        for line in open(f, encoding='utf-8'):
            p = line.split()
            if len(p) >= 9: r[p[0]] = p
        return r
    A, B = load(fa), load(fb)
    dists, anim_diff, flip_diff, nav_diff, rot_diff = [], [], [], [], []
    for k, a in A.items():
        b = B.get(k)
        if not b: continue
        dist = math.hypot(float(a[2]) - float(b[2]), float(a[3]) - float(b[3]))
        dists.append((dist, a[1], a[4], b[4]))
        if a[4] != b[4]: anim_diff.append((a[1], a[4], b[4]))
        if a[5] != b[5] or a[6] != b[6]: flip_diff.append((a[1], a[4], a[5] + a[6], b[5] + b[6], a[7]))
        if a[7] != b[7]: nav_diff.append((a[1], a[7], b[7]))
        if a[8] != b[8]: rot_diff.append((a[1], a[8], b[8]))
    dists.sort(reverse=True)
    ds = [x[0] for x in dists]
    print(f"sync{n}: {len(ds)} matched (host {len(A)}, guest {len(B)}); pos err mean {sum(ds)/len(ds):.3f}, >0.5: {sum(1 for x in ds if x > 0.5)}, >1: {sum(1 for x in ds if x > 1)}, max {ds[0]:.2f} ({dists[0][1]} {dists[0][2]}/{dists[0][3]})")
    print(f"   anim differs {len(anim_diff)}: {anim_diff[:5]}")
    print(f"   flip differs {len(flip_diff)}: {flip_diff[:6]}")
    print(f"   nav differs {len(nav_diff)}: {nav_diff[:6]}  rot differs {len(rot_diff)}: {rot_diff[:4]}")

# ---- objects (EntitySync): <name>_syncN_entities.txt, both taken at the same point of the host's record stream
for n in range(1, 10):
    fa, fb = os.path.join(d, f"{HOST}_sync{n}_entities.txt"), os.path.join(d, f"Bob_sync{n}_entities.txt")
    if not (os.path.exists(fa) and os.path.exists(fb)): continue
    def load_e(f):
        r, local = {}, 0
        for line in open(f, encoding='utf-8'):
            if line.startswith('# local'): local = int(line.split()[2]); continue
            p = line.split()
            if len(p) >= 8: r[p[0]] = p
        return r, local
    A, la = load_e(fa)
    B, lb = load_e(fb)
    from collections import Counter
    only_a = [A[k] for k in A if k not in B]
    only_b = [B[k] for k in B if k not in A]
    tag_diff = [(A[k][1], B[k][1]) for k in A if k in B and A[k][1] != B[k][1]]
    cont_diff, pos_diff, mass_diff = [], [], []
    for k in A:
        if k not in B or A[k][1] != B[k][1]: continue
        a, b = A[k], B[k]
        if a[5] != b[5] or a[6] != b[6]: cont_diff.append((a[1], a[5], b[5])); continue
        # (meteors in flight: compared at the timeline's moment in the meteors section below)
        if a[7] == '0' and a[5] == '-1' and not a[1].endswith('Comet') and math.hypot(float(a[2]) - float(b[2]), float(a[3]) - float(b[3])) > 0.6:
            pos_diff.append((a[1], a[2], a[3], b[2], b[3]))
        if a[4] != '-' and b[4] != '-':
            ma, mb = float(a[4]), float(b[4])
            if abs(ma - mb) > max(0.02 * max(ma, mb), 0.01): mass_diff.append((a[1], a[4], b[4]))
    top = lambda rows: Counter(r[1] for r in rows).most_common(5)
    print(f"objects sync{n}: host {len(A)}, guest {len(B)} (+{lb} local); missing on guest {len(only_a)} {top(only_a)}; "
          f"extra on guest {len(only_b)} {top(only_b)}; other prefab {len(tag_diff)}; container differs {len(cont_diff)} {cont_diff[:3]}; "
          f"loose item pos differs {len(pos_diff)} {pos_diff[:3]}; mass differs {len(mass_diff)} {mass_diff[:3]}")

# ---- buildings (BuildingSync): animations / meters, pipes in view, equipment
for n in range(1, 10):
    fa, fb = os.path.join(d, f"{HOST}_sync{n}_buildings.txt"), os.path.join(d, f"Bob_sync{n}_buildings.txt")
    if not (os.path.exists(fa) and os.path.exists(fb)): continue
    def load_b(f):
        anims, pipes, equip = {}, {}, set()
        rails.clear()
        for line in open(f, encoding='utf-8'):
            p = line.split()
            if p[0] == 'A' and len(p) >= 7: anims[(p[1], p[3])] = p
            elif p[0] == 'P' and len(p) >= 5: pipes[(p[1], p[2])] = p
            elif p[0] == 'E' and len(p) >= 4: equip.add((p[1], p[2], p[3]))
            elif p[0] == 'R' and len(p) >= 3: rails[p[1]] = p[2]
        return anims, pipes, equip
    from collections import Counter
    rails = {}
    aa, pa, ea = load_b(fa); ra = dict(rails)
    ab, pb, eb = load_b(fb); rb = dict(rails)
    # animations are compared when the guest's timeline shows the test moment (<name>_syncN_anims.txt, newer runs)
    fa2, fb2 = os.path.join(d, f"{HOST}_sync{n}_anims.txt"), os.path.join(d, f"Bob_sync{n}_anims.txt")
    if os.path.exists(fa2) and os.path.exists(fb2):
        aa = load_b(fa2)[0]; ab = load_b(fb2)[0]
    rail_diff = [(c, ra[c], rb.get(c)) for c in ra if ra[c] != rb.get(c)]
    both = [k for k in aa if k in ab]
    anim_diff = [(aa[k][2], aa[k][4], ab[k][4]) for k in both if aa[k][4] != ab[k][4] or aa[k][5] != ab[k][5]]
    meter_diff = [(aa[k][2], aa[k][6], ab[k][6]) for k in both if aa[k][4] == ab[k][4] and float(aa[k][6]) >= 0 and abs(float(aa[k][6]) - float(ab[k][6])) > 0.05]
    pboth = [k for k in pa if k in pb]
    pipe_diff = [(pa[k][3], pa[k][4], pb[k][3], pb[k][4]) for k in pboth
                 if pa[k][3] != pb[k][3] or abs(float(pa[k][4]) - float(pb[k][4])) > max(0.05 * max(float(pa[k][4]), float(pb[k][4])), 0.001)]
    print(f"buildings sync{n}: {len(both)} animations compared, differ {len(anim_diff)} {Counter(x[0] for x in anim_diff).most_common(4)} {anim_diff[:3]}; "
          f"meters differ {len(meter_diff)} {meter_diff[:3]}; pipe cells {len(pboth)}, differ {len(pipe_diff)} {pipe_diff[:3]}; "
          f"worn: host {len(ea)}, guest {len(eb)}, differ {len(ea ^ eb)} {sorted(ea ^ eb)[:4]}; "
          f"rails: {len(ra)} cells, {sum(1 for v in ra.values() if v != '-1')} items on host, {sum(1 for v in rb.values() if v != '-1')} on guest, differ {len(rail_diff)} {rail_diff[:3]}")

# ---- UI settings (Calls): capacities, doors, enabled, fabricator queues, assignments, research, priorities, food, schedules
for n in range(1, 10):
    fa, fb = os.path.join(d, f"{HOST}_sync{n}_settings.txt"), os.path.join(d, f"Bob_sync{n}_settings.txt")
    if not (os.path.exists(fa) and os.path.exists(fb)): continue
    def load_s(f):
        r = {}
        for line in open(f, encoding='utf-8'):
            p = line.split()
            if len(p) >= 2: r[' '.join(p[:-1]) if p[0] not in ('RES',) else 'RES'] = p[-1]
        return r
    A, B = load_s(fa), load_s(fb)
    diff = sorted(k for k in set(A) | set(B) if A.get(k) != B.get(k))
    from collections import Counter
    print(f"settings sync{n}: {len(A)} host / {len(B)} guest values, differ {len(diff)} {Counter(k.split()[0] for k in diff).most_common(6)} "
          f"{[(k, A.get(k), B.get(k)) for k in diff[:4]]}")

# ---- global (GlobalSync): duplicant amounts, experience, research, clock, printing pod
for n in range(1, 10):
    fa, fb = os.path.join(d, f"{HOST}_sync{n}_global.txt"), os.path.join(d, f"Bob_sync{n}_global.txt")
    if not (os.path.exists(fa) and os.path.exists(fb)): continue
    def load_g(f):
        r = {}
        for line in open(f, encoding='utf-8'):
            p = line.split()
            if not p: continue
            if p[0] == 'AMT': r[('AMT', p[1], p[2])] = (float(p[3]), float(p[4]))
            elif p[0] == 'XP': r[('XP', p[1])] = (float(p[2]), float(p[3]))
            elif p[0] == 'PROG': r[('PROG', p[1], p[2])] = (float(p[3]), 0)
            else: r[(p[0],)] = tuple(float(x) for x in p[1:])
        return r
    A, B = load_g(fa), load_g(fb)
    bad = []
    for k in sorted(set(A) | set(B)):
        a, b = A.get(k), B.get(k)
        if a is None or b is None: bad.append((k, a, b)); continue
        if k[0] == 'AMT': ok = abs(a[0] - b[0]) <= max(0.02 * a[1], 0.5)
        elif k[0] == 'XP': ok = abs(a[0] - b[0]) <= 5 and a[1] == b[1]
        elif k[0] == 'PROG': ok = abs(a[0] - b[0]) <= 1
        elif k[0] == 'CLOCK': ok = a[0] == b[0] and abs(a[1] - b[1]) <= 3
        elif k[0] == 'PRINT': ok = abs(a[0] - b[0]) <= 3 or (a[0] > 1e9 and b[0] > 1e9)
        else: ok = a == b
        if not ok: bad.append((k, a, b))
    from collections import Counter
    print(f"global sync{n}: {len(A)} values, differ {len(bad)} {Counter(k[0] if k[0] != 'AMT' else 'AMT ' + k[2] for k, _, _ in bad).most_common(6)} {bad[:3]}")

# ---- status items and notifications (what each side shows; not yet synced)
for n in range(1, 10):
    fa, fb = os.path.join(d, f"{HOST}_sync{n}_status.txt"), os.path.join(d, f"Bob_sync{n}_status.txt")
    if not (os.path.exists(fa) and os.path.exists(fb)): continue
    def load_st(f):
        st, notes = {}, Counter()
        fx.clear()
        for line in open(f, encoding='utf-8'):
            line = line.rstrip('\n')
            if line.startswith('F '):
                p = line.split(' ', 3)
                fx[p[1]] = [(x.rsplit(':', 1)[0], float(x.rsplit(':', 1)[1])) for x in (p[3] if len(p) > 3 else '').split(',') if x]
            elif line.startswith('S '):
                p = line.split(' ', 3)
                st[p[1]] = (p[2], set(x for x in (p[3] if len(p) > 3 else '').split(',') if x))
            elif line.startswith('N '): notes[line[2:]] += 1
        return st, notes
    from collections import Counter
    fx = {}
    sa, na = load_st(fa); fa_ = dict(fx)
    sb, nb = load_st(fb); fb_ = dict(fx)
    fx_only_h, fx_only_g, fx_time = Counter(), Counter(), []
    for k, a in fa_.items():
        b = fb_.get(k, [])
        ca, cb = Counter(e for e, _ in a), Counter(e for e, _ in b)
        fx_only_h.update(ca - cb); fx_only_g.update(cb - ca)
        ta, tb = dict(a), dict(b)
        for e in ta.keys() & tb.keys(): fx_time.append(abs(ta[e] - tb[e]))
    for k, b in fb_.items():
        if k not in fa_:
            for e, _ in b: fx_only_g[e] += 1
    host_only, guest_only, ents = Counter(), Counter(), 0
    for k, (prefab, a) in sa.items():
        if k not in sb: continue
        b = sb[k][1]
        if a != b: ents += 1
        for x in a - b: host_only[x] += 1
        for x in b - a: guest_only[x] += 1
    print(f"status sync{n}: {len(sa)} objects, {ents} differ; only on host: {host_only.most_common(6)}; only on guest: {guest_only.most_common(6)}")
    fx_time.sort()
    print(f"   effects: host {sum(len(v) for v in fa_.values())} on {len(fa_)} objects, guest {sum(len(v) for v in fb_.values())}; "
          f"only host {fx_only_h.most_common(5)}; only guest {fx_only_g.most_common(5)}; "
          f"time left differs: median {fx_time[len(fx_time)//2] if fx_time else 0:.0f} s, max {fx_time[-1] if fx_time else 0:.0f} s")
    print(f"   notifications: host {sum(na.values())}, guest {sum(nb.values())}; only host {list((na - nb).items())[:4]}; only guest {list((nb - na).items())[:4]}")

# ---- the cycle report (today's and the last finished one)
for n in range(1, 10):
    fa, fb = os.path.join(d, f"{HOST}_sync{n}_report.txt"), os.path.join(d, f"Bob_sync{n}_report.txt")
    if not (os.path.exists(fa) and os.path.exists(fb)): continue
    def load_r(f):
        out = {}
        for line in open(f, encoding='utf-8'):
            p = line.split()
            if len(p) >= 6: out[(p[0], p[1], p[2])] = (float(p[3]), float(p[4]), int(p[5]))
        return out
    ra, rb = load_r(fa), load_r(fb)
    diff = []
    for k, (pa, na, ca) in ra.items():
        pb, nb, cb = rb.get(k, (0.0, 0.0, 0))
        tol = lambda x, y: abs(x - y) <= max(0.02 * max(abs(x), abs(y)), 0.01)
        if not (tol(pa, pb) and tol(na, nb)) or ca != cb: diff.append((k[0] + k[1], k[2], f"{pa:g}/{na:g}/{ca}", f"{pb:g}/{nb:g}/{cb}"))
    nz = sum(1 for v in ra.values() if v[0] or v[1])
    print(f"report sync{n}: {len(ra)} entries ({nz} non-zero on host), differ {len(diff)}: {diff[:4]}")

# ---- puppet looks (PuppetLooks): overrides (tools, clothes), hidden symbols, face, work context; taken with the puppet dump
for n in range(1, 10):
    fa, fb = os.path.join(d, f"{HOST}_sync{n}_looks.txt"), os.path.join(d, f"Bob_sync{n}_looks.txt")
    if not (os.path.exists(fa) and os.path.exists(fb)): continue
    def load_l(f):
        r = {}
        for line in open(f, encoding='utf-8'):
            p = line.split()
            if len(p) < 6: continue
            r[p[0]] = {'kind': p[1], **{x.split('=', 1)[0]: x.split('=', 1)[1] for x in p[2:] if '=' in x}}
        return r
    A, B = load_l(fa), load_l(fb)
    from collections import Counter
    diff = Counter()
    ex = {}
    tools_host = 0
    rgt = format(0, 'x')
    for k, a in A.items():
        b = B.get(k)
        if not b: continue
        for part in ('ov', 'hidden', 'expr', 'ctx', 'thoughts'):
            if a.get(part) != b.get(part):
                diff[part] += 1
                if part == 'ov':
                    sa, sb_ = set(a['ov'].split(',')), set(b['ov'].split(','))
                    ex.setdefault(part, (a['kind'], sorted(sa - sb_)[:2], sorted(sb_ - sa)[:2]))
                else:
                    ex.setdefault(part, (a['kind'], a.get(part, '')[:80], b.get(part, '')[:80]))
        if a.get('ctx', '-') != '-': tools_host += 1
    print(f"looks sync{n}: {len(A)} puppets (guest {len(B)}), working with a context on host {tools_host}; differ {dict(diff)} {ex}")

# ---- building states, batteries, lights, circuits, automation (OperationSync)
for n in range(1, 10):
    fa, fb = os.path.join(d, f"{HOST}_sync{n}_operation.txt"), os.path.join(d, f"Bob_sync{n}_operation.txt")
    if not (os.path.exists(fa) and os.path.exists(fb)): continue
    def load_o(f):
        r = {}
        for line in open(f, encoding='utf-8'):
            p = line.split()
            if not p: continue
            if p[0] == 'OP': r[('OP', p[1])] = (p[2], p[3], p[4] if len(p) > 4 else '')
            elif p[0] == 'BAT': r[('BAT', p[1])] = (float(p[2]), float(p[3]))
            elif p[0] == 'LIGHT': r[('LIGHT', p[1])] = tuple(p[2:])
            elif p[0] == 'CIRCUIT': r[('CIRCUIT', p[1])] = tuple(float(x) for x in p[2:])
            elif p[0] == 'LOGIC': r[('LOGIC', p[1])] = p[2]
            elif p[0] == 'GEN': r[('GEN', p[1])] = tuple(p[2:])
        return r
    A, B = load_o(fa), load_o(fb)
    from collections import Counter
    cnt, bad = Counter(), Counter()
    ex = {}
    for k, a in A.items():
        cnt[k[0]] += 1
        b = B.get(k)
        if b is None: bad[k[0] + ' missing'] += 1; continue
        if k[0] == 'OP':
            ok = a[1] == b[1]
            if a[1] == b[1] and a[2] != b[2]:
                # flags only the guest's code has (set true: neutral) don't count
                fa_ = dict(x.split('=') for x in a[2].split(',') if '=' in x)
                fb_ = dict(x.split('=') for x in b[2].split(',') if '=' in x)
                ok = all(fb_.get(f) == v for f, v in fa_.items()) and all(v == '1' for f, v in fb_.items() if f not in fa_)
            key = 'OP'
        elif k[0] == 'BAT': ok = abs(a[0] - b[0]) <= max(0.02 * a[1], 1.0); key = 'BAT'
        elif k[0] == 'CIRCUIT':
            ok = abs(a[0] - b[0]) <= max(0.05 * abs(a[0]), 1) and abs(a[1] - b[1]) < 1 and abs(a[2] - b[2]) < 1 \
                 and abs(a[3] - b[3]) <= max(0.02 * a[3], 5) and abs(a[4] - b[4]) <= 0.02
            key = 'CIRCUIT'
        else: ok = a == b; key = k[0]
        if not ok:
            bad[key] += 1
            ex.setdefault(key, [])
            if len(ex[key]) < 3: ex[key].append((k[1], a, b))
    print(f"operation sync{n}: {dict(cnt)}; differ {dict(bad)}")
    for key, v in ex.items(): print(f"   {key}: {v}")

# ---- meteors in flight (Meteors), taken when the guest's timeline shows the test moment
for n in range(1, 10):
    fa, fb = os.path.join(d, f"{HOST}_sync{n}_movers.txt"), os.path.join(d, f"Bob_sync{n}_movers.txt")
    if not (os.path.exists(fa) and os.path.exists(fb)): continue
    def load_m(f):
        r = {}
        for line in open(f, encoding='utf-8'):
            p = line.split()
            if len(p) >= 5 and p[0] == 'M': r[p[1]] = (p[2], float(p[3]), float(p[4]))
        return r
    A, B = load_m(fa), load_m(fb)
    both = [k for k in A if k in B]
    errs = sorted((math.hypot(A[k][1] - B[k][1], A[k][2] - B[k][2]), A[k][0]) for k in both)
    print(f"meteors sync{n}: host {len(A)}, guest {len(B)}, matched {len(both)}; only host {len(set(A) - set(B))}, only guest {len(set(B) - set(A))}; "
          f"position error max {errs[-1][0] if errs else 0:.2f} mean {sum(e for e, _ in errs) / len(errs) if errs else 0:.2f}")
