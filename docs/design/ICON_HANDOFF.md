# CabinetOS App Icon — Handoff

## Changelog
- v1 · initial icon production specs


Final icon: concept **2e** ("corner badge"), refined in section 3 of `CabinetOS Icons.dc.html`.
A blue Windows 11 folder with a dark terminal tile overlapping the bottom-right corner, separated by a white knockout ring.

## Files (`icons/`)
| File | Use |
|---|---|
| `cabinetos.svg` | Master, 128-unit viewBox rendered at 256. Full shading. Source for 256 and larger. |
| `cabinetos-48.svg` / `.png` | 48 px cut: light shading, lip highlight, badge 52×50 with 5 px ring. |
| `cabinetos-32.svg` / `.png` | 32 px cut: flat gradients, badge 58×56 with 6 px ring. |
| `cabinetos-24.svg` / `.png` | 24 px cut: flat fills, badge 68×66 with 7 px ring, no prompt underline. |
| `cabinetos-16.svg` / `.png` | 16 px cut: flat fills, badge 76×74 with 8 px ring, prompt stroke 16. |
| `cabinetos-256.png` | Raster of the master. |

Rule: **never downscale the master**. Use the size-specific cut for each target; the badge grows and the prompt thickens as size drops so the mark stays legible.

## Windows packaging
Build a multi-image `.ico` containing 16, 24, 32, 48, 256 (256 as PNG-compressed). For MSIX `Assets/`, render from the master at scale factors 100–400 for Square44x44Logo, Square150x150Logo, Wide310x150Logo, StoreLogo; use the 16/24/32/48 cuts for the `targetsize-*` variants (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256: choose the nearest cut at or below the target and render at exact size). Provide `altform-unplated` and `altform-lightunplated` copies for the taskbar (identical artwork; the ring already handles both bars).

## Geometry (128-unit grid)
- Folder back: path `M10 32 a8 8 0 0 1 8-8 h28 l10 10 h44 a8 8 0 0 1 8 8 v50 a8 8 0 0 1-8 8 H18 a8 8 0 0 1-8-8 z`.
- Folder front: `M10 48 a6 6 0 0 1 6-6 h86 a6 6 0 0 1 6 6 v42 a10 10 0 0 1-10 10 H20 a10 10 0 0 1-10-10 z`.
- Badge (master): rect 64,64 56×54 r14 white; inner rect 68,68 48×46 r11 dark. Prompt chevron `M80 82 l9 9 -9 9`, stroke 5.5, round caps/joins; underline rect 94,96 14×5 r2.5.
- Safe area: artwork occupies 10–120 horizontally, 24–118 vertically (badge shadow may extend to 122).

## Colors
| Part | Master | Flat (≤32 px) |
|---|---|---|
| Folder back | linear #5AA9FF → #1B56B8 | #2D6FD0 |
| Folder front | linear #A6D8FF → #5FA8F0 → #3A86E0 + top white highlight .55→0 | #5EA8F0 |
| Front lip | white .7, 1 unit | white .6 (48 only) |
| Badge ring | #FFFFFF | #FFFFFF |
| Terminal tile | linear #343A48 → #12151D + top highlight white .18 | #1A1D26 |
| Prompt | linear #B4FFD0 → #5FE89A | #7CF4A8 |
| Underline | white .92 | white .9 |
| Folder shadow | 0 3 blur 3 #0A2A5C .35 | none |
| Badge shadow | 0 2.5 blur 2.5 #000 .45 | none |

## Do / Don't
- Do keep the ring white on both dark and light surfaces; it is what separates badge from folder.
- Do keep the folder blue. Yellow reads as Explorer.
- Don't add text, the app name, or a third color.
- Don't recolor the prompt to match the accent; green is the terminal signal.
- Monochrome variant (notification area, high contrast): white folder silhouette with the badge as a knockout hole and the chevron cut out of it.

## Alternates considered
Concepts 1a–1e and 2a–2d are in `CabinetOS Icons.dc.html` for reference. 2a (prompt debossed on flap) is the fallback if the badge proves too busy at 16 px in real rendering.
