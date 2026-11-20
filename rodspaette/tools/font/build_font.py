"""Builds Rodspaette Script from the traced letters (wordmark.py + pen.py).
Design grid: reference-photo pixels (cap height 105 px, baseline y=123, y down) -> font units (cap height 700, y up).
'PDF' typed together becomes one overlapped glyph through a standard ligature, like the DSB monogram."""
import sys, os
from shapely import affinity
from shapely.ops import unary_union
from shapely.geometry import MultiPolygon
from shapely.geometry.polygon import orient
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPen
from fontTools.feaLib.builder import addOpenTypeFeaturesFromString
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pen import glyph
from wordmark import LETTERS

U = 700 / 105            # font units per design pixel
BASE = 123
SB = 2.0                 # side bearing each side, design px
SPACE = 34
OVERLAP = 42             # how far P, D and F slide into each other in the PDF ligature
NAMES = {"R": "R", "P": "P", "D": "D", "F": "F", "d": "d", "s": "s", "p": "p", "æ": "ae", "t": "t", "e": "e", "ø": "oslash"}

def to_font(g):
    g = affinity.scale(g, xfact=U, yfact=-U, origin=(0, 0))
    return affinity.translate(g, 0, BASE * U)

def ttglyph(g):
    pen = TTGlyphPen(None)
    g = g.simplify(0.4, preserve_topology=True)
    for poly in (g.geoms if isinstance(g, MultiPolygon) else [g]):
        poly = orient(poly, sign=-1.0)
        for ring in [poly.exterior, *poly.interiors]:
            pts = [(round(x), round(y)) for x, y in list(ring.coords)[:-1]]
            pts = [p for i, p in enumerate(pts) if p != pts[i - 1]]
            if len(pts) < 3: continue
            pen.moveTo(pts[0])
            for p in pts[1:]: pen.lineTo(p)
            pen.closePath()
    return pen.glyph()

def build(path, w_h, w_v, bold):
    shapes, metrics = {}, {}
    for ch, name in NAMES.items():
        g = glyph(LETTERS[ch], w_h, w_v)
        minx = g.bounds[0]
        g = affinity.translate(g, -minx + SB, 0)
        shapes[name] = g
        metrics[name] = g.bounds[2] - g.bounds[0] + 2 * SB    # advance in design px
    # the overlapped PDF
    x = 0; parts = []
    for i, n in enumerate(("P", "D", "F")):
        parts.append(affinity.translate(shapes[n], x, 0))
        x += metrics[n] - (OVERLAP if i < 2 else 0)
    shapes["P_D_F"] = unary_union(parts); metrics["P_D_F"] = x

    order = [".notdef", "space"] + list(shapes.keys())
    fb = FontBuilder(1000, isTTF=True)
    fb.setupGlyphOrder(order)
    fb.setupCharacterMap({32: "space", **{ord(c): n for c, n in NAMES.items()}})
    nd = TTGlyphPen(None)
    nd.moveTo((50, 0)); nd.lineTo((50, 700)); nd.lineTo((350, 700)); nd.lineTo((350, 0)); nd.closePath()
    glyf = {".notdef": nd.glyph(), "space": TTGlyphPen(None).glyph()}
    hmtx = {".notdef": (400, 50), "space": (round(SPACE * U), 0)}
    for n, g in shapes.items():
        fg = to_font(g)
        glyf[n] = ttglyph(fg)
        hmtx[n] = (round(metrics[n] * U), round(fg.bounds[0]))
    fb.setupGlyf(glyf)
    fb.setupHorizontalMetrics(hmtx)
    fb.setupHorizontalHeader(ascent=950, descent=-330)
    style = "Bold" if bold else "Regular"
    fb.setupNameTable({"familyName": "Rodspaette Script", "styleName": style,
                       "uniqueFontIdentifier": f"RodspaetteScript-{style}-2.0", "fullName": "Rodspaette Script" + (" Bold" if bold else ""),
                       "psName": f"RodspaetteScript-{style}", "version": "Version 2.000",
                       "designer": "Traced from a DSB carriage monogram"})
    fb.setupOS2(sTypoAscender=950, sTypoDescender=-330, sTypoLineGap=0, usWinAscent=1000, usWinDescent=350,
                sxHeight=420, sCapHeight=700, achVendID="RDSP", usWeightClass=700 if bold else 400,
                fsSelection=0x20 if bold else 0x40)
    fb.setupPost()
    fb.setupHead(unitsPerEm=1000, macStyle=1 if bold else 0)
    addOpenTypeFeaturesFromString(fb.font, "languagesystem DFLT dflt; languagesystem latn dflt;\nfeature liga { sub P D F by P_D_F; } liga;\n")
    fb.save(path)

if __name__ == "__main__":
    out = sys.argv[1] if len(sys.argv) > 1 else "."
    build(os.path.join(out, "RodspaetteScript-Regular.ttf"), 6.0, 8.0, False)
    build(os.path.join(out, "RodspaetteScript-Bold.ttf"), 7.4, 9.8, True)
    print("built")
