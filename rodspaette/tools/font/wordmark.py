"""Rødspætte PDF letters drawn by the rules taken from the traced D, S, B.
Same pixel grid as the reference crop: baseline y=123, cap top y=18 (cap height 105), x-height top y=60, y down."""
import re
from trace_ref import D as REF_D, S as REF_S, B as REF_B

def xform(d, f):
    toks = re.findall(r"[MC]|-?\d+(?:\.\d+)?", d); out = []; i = 0
    while i < len(toks):
        t = toks[i]
        if t in "MC": out.append(t); i += 1; continue
        x, y = f(float(toks[i]), float(toks[i+1])); out += ["%.1f" % x, "%.1f" % y]; i += 2
    return " ".join(out)

# lowercase s = the reference S drawn to x-height (sharp turns kept)
s_lower = [(xform(d, lambda x, y: ((x - 112) * 0.52 + 8, (y - 22) * 0.6 + 58)), kw) for d, kw in REF_S]

TOP_ENTRY = "M 178 12.5 C 186.5 21.5 204.5 17 224.5 13 C 247.5 9 272 11.5 283 21.5 C 294 32.5 300.5 52 281.5 63.5"   # B's entry flowing into its top bowl
STEM_B = REF_B[1]   # the bent B stem: angled head, plumb foot
FOOT = lambda x: ("M %d 131 C %d 124 %d 121 %d 121 C %d 121 %d 121 %d 118" % (x, x + 8, x + 18, x + 30, x + 38, x + 44, x + 50), dict(start=0.5, end=0.85))

LETTERS = {
    "D": [(d, kw) for d, kw in REF_D],
    "P": [(TOP_ENTRY + " C 271.5 70.5 250 72 228 69 C 220 68 213 67 207 65", dict(start=0.6, end=0.7)), STEM_B, FOOT(178)],
    "R": [(TOP_ENTRY + " C 271.5 70.5 250.5 70.5 246.5 65.5 C 244.5 60.5 266.5 56.5 283.5 64.5 C 296 72 300 86 304 99 C 308 111 315 119 328 122",
           dict(start=0.6, end=0.6)), STEM_B, FOOT(178)],
    "F": [("M 11 33 C 23 42 39 38 56 30 C 76 20 110 15 140 19 C 152 21 160 25 166 31", dict(start=1.25, end=0.6)),
          REF_D[1],
          ("M 38 74 C 60 70 86 72 110 77", dict(start=0.8, end=0.6)),
          FOOT(26)],
    "d": [("M 87 74 C 77 62 61 59 49 61 C 31 65 22 84 22 100 C 22 115 32 124 47 124 C 64 124 80 116 89 104", dict(start=0.8, end=0.7)),
          ("M 62 16 C 70 12 78 11 82 14 C 93 42 94 92 89 124", dict(start=0.6, end=0.75))],
    "s": s_lower,
    "p": [("M 14 66 C 20 62 26 60 31 61 C 40 88 41 128 35 160", dict(start=0.6, end=0.6)),
          ("M 32 76 C 40 64 54 59 66 61 C 82 64 90 80 90 96 C 90 113 80 124 64 124 C 52 124 40 118 34 108", dict(start=0.8, end=0.7))],
    "æ": [("M 79 72 C 70 62 55 59 44 61 C 28 65 20 84 20 100 C 20 115 30 124 45 124 C 60 124 74 115 80 102", dict(start=0.8, end=0.7)),
          ("M 77 92 C 97 92 119 90 129 84 C 137 78 135 64 121 61 C 105 58 88 64 80 76", dict(start=0.8, end=0.7)),
          ("M 75 62 C 83 78 83 100 80 116 C 81 122 90 124 100 124 C 114 124 124 119 132 110", dict(start=0.8, end=0.6))],
    "t": [("M 26 30 C 37 54 39 82 35 112 C 36 122 46 125 54 123 C 60 121 64 117 66 112", dict(start=0.7, end=0.6)),
          ("M 10 64 C 26 61 44 61 62 63", dict(start=0.8, end=0.6))],
    "e": [("M 18 92 C 38 92 60 90 70 84 C 78 78 76 64 62 61 C 42 58 22 72 20 94 C 18 114 30 124 46 124 C 58 124 68 119 74 110", dict(start=0.8, end=0.6))],
    "ø": [("M 50 60 C 30 60 18 78 18 94 C 18 112 30 124 48 124 C 66 124 80 108 80 92 C 80 74 68 60 50 60", dict(start=1.0, end=1.0)),
          ("M 12 132 C 30 112 60 75 88 50", dict(start=0.7, end=0.6))],
}
