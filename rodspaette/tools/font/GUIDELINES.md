# Rodspaette Script — design rules (version 2)

Traced from the D·S·B monogram on an old DSB luxury carriage (c. 1900: late-Victorian ornamental lettering
meeting Danish Art Nouveau, *Skønvirke*). `trace_ref.py` holds D, S and B as traced in the DSB letter tracer;
every other letter follows the rules below. Version 2.000 covers **R ø d s p æ t e P D F** and the space.

## Pen
1. Round pen with mild pressure: vertical strokes about a third heavier than horizontal ones (vertical stress).
   Regular: 6 / 8 px on a 105 px cap height (40 / 53 font units). Bold: 7.4 / 9.8 px.
2. A stroke starts firm and lifts off thinner.

## Anatomy
3. **Bent stems.** A stem bows out to the right: angled at the head, fullest at mid-height, the foot turning
   slightly back. Never ruler-straight.
4. **Flat spine with sharp corners.** The S spine is long and nearly straight and meets its bowls at sharp corners.
5. **Bowls** are wide and lopsided, fullest below the middle; the B's lower bowl is larger than its upper one.
6. **Looped junction.** The B joins its bowls through a small closed loop lying on its side (copperplate habit).
   R uses the same loop, with the leg leaving from it.
7. **Terminals, two kinds only.**
   - *Hairpin hooks*: the stroke reverses sharply into a short return that bows slightly — both ends of the S
     (upper hook up and to the left, bowing toward the bottom left; lower hook turning down, bowing right).
     The bar before each hook curves gently into it (the S top dips after its hook; the base rises before its hook).
   - *Tapered bows*: the stroke simply thins and curves away (D's entry and foot, B's lead-in, the B's base).
   No curls back on themselves, no ball terminals.
8. **Lead-ins.** Capitals approach from the left on a thin line that flows unbroken into the first bowl (B, P, R)
   or sweeps across the stem head (D, F).
9. **Feet.** Capitals stand on the D's swash foot: running left past the stem, lifting at its left end.
10. **One curve per stroke.** At most one change of direction; no bumps.
11. **Overlap is composition.** Letters may cross and share space (the monogram, and the PDF ligature), but a
    single letter never contains a partner's strokes.

## Lowercase (the reference has none)
Same pen and bent stems; d and p start with a thin lead-in; s is the traced S at x-height with both hooks;
e, æ and t end in tapered bows. x-height 0.6 × cap height.

## PDF
Typed together, P D F become one overlapped glyph (standard ligature `liga`): each letter slides 42 px into the
one before, like the D·S·B monogram.

## Building
    pip install fonttools shapely
    python3 build_font.py ../../src/Rodspaette.App/Assets/Fonts
