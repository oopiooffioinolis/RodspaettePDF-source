"""Variable-width pen: thicker on vertical strokes, a firm start and a tapered finish, like the reference."""
import math, re
from shapely.geometry import Polygon, Point, MultiPolygon
from shapely.ops import unary_union

def bez_points(d, step=0.5):
    toks = re.findall(r"[MC]|-?\d+(?:\.\d+)?", d)
    pts, i, cur = [], 0, None
    while i < len(toks):
        t = toks[i]; i += 1
        if t == "M":
            cur = (float(toks[i]), float(toks[i+1])); i += 2; pts.append(cur)
        else:
            while i < len(toks) and re.match(r"-?\d", toks[i]):
                p1 = (float(toks[i]), float(toks[i+1])); p2 = (float(toks[i+2]), float(toks[i+3])); p3 = (float(toks[i+4]), float(toks[i+5])); i += 6
                L = math.dist(cur, p1) + math.dist(p1, p2) + math.dist(p2, p3)
                n = max(6, int(L / step))
                for k in range(1, n+1):
                    u = k/n; a=(1-u)**3; b=3*(1-u)**2*u; c=3*(1-u)*u*u; e=u**3
                    pts.append((a*cur[0]+b*p1[0]+c*p2[0]+e*p3[0], a*cur[1]+b*p1[1]+c*p2[1]+e*p3[1]))
                cur = p3
    return pts

def stroke(d, w_h, w_v, start=1.0, end=0.55, ramp=0.10, step=0.5):
    """w_h: width on horizontal runs, w_v: on vertical runs. start/end: width factors at the two ends."""
    pts = bez_points(d, step)
    n = len(pts)
    seg = [math.dist(pts[k], pts[k+1]) for k in range(n-1)]
    total = sum(seg) or 1
    s = [0.0]
    for L in seg: s.append(s[-1] + L)
    widths = []
    for k in range(n):
        a = pts[max(0, k-3)]; b = pts[min(n-1, k+3)]
        ang = math.atan2(b[1]-a[1], b[0]-a[0])
        base = w_h + (w_v - w_h) * abs(math.sin(ang))
        t = s[k] / total
        f = 1.0
        if t < ramp: f = start + (1 - start) * (t / ramp)
        elif t > 1 - ramp: f = end + (1 - end) * ((1 - t) / ramp)
        widths.append(base * f)
    caps = []
    for k in range(n - 1):
        c1 = Point(pts[k]).buffer(widths[k] / 2, quad_segs=8)
        c2 = Point(pts[k+1]).buffer(widths[k+1] / 2, quad_segs=8)
        caps.append(c1.union(c2).convex_hull)
    return unary_union(caps)

def glyph(strokes, w_h, w_v):
    return unary_union([stroke(d, w_h, w_v, **kw) for d, kw in strokes])
