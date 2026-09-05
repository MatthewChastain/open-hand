#!/usr/bin/env python3
"""Recreate the vanilla hotbar dialog background, pixel-exactly.

Everything here is transcribed from the decompiled Vintage Story 1.22.7
sources (decompile tree: Optimum-0.3.14, matching the installed client):

- GuiElementDialogBackground.ComposeElements  (fill / glow stroke / blur /
  grain fill / border stroke, in that order)
- HudHotbar (line 271): the hotbar's background element has FullBlur = true,
  so the blur is surface.BlurFull(scaled(9))
- Cairo SurfaceTransformBlur.GaussianBlur: boxesForGauss(sigma, 3) followed by
  three box passes (horizontal + vertical each) that touch ONLY the RGB
  channels - alpha is copied through untouched - with edge-clamped sampling
- GuiElement.scaled(v) = v * RuntimeEnv.GUIScale (GUIScale = 1 here; the
  texture is stretched uniformly by the mod at other scales)
- GuiElement.getPattern: soil.png with mulAlpha=64 (~25% alpha),
  Filter.Nearest, pattern matrix scale 0.125/GUIScale. soil.png is 32x32 and
  Cairo samples pattern space with the INVERSE of the matrix, so the tile is
  drawn at 32 / (0.125) = 256 px - an 8x nearest-neighbor blowup.
- GuiStyle: DialogStrongBgColor = #403529 (alpha 1), DialogLightBgColor =
  #403529 @ 0.75, DialogBGRadius = 1. The glow stroke color is
  (Light[0]*2.1, Strong[1]*2.1, Strong[2]*2.1) with alpha 1 and line width
  strokeWidth*2 = 10; the border stroke is rgba(45, 35, 33, Alpha*Alpha =
  0.5625) with line width strokeWidth = 5.

The hotbar's dialog surface is fully covered by its background path (the path
runs from (0,0) to (W, H-1), so the strokes clip at the surface edges) - the
composed surface is opaque and the blur edge-clamps on every side.

The Open Hand extension panel is the LEFT END of that same bar: identical rim
treatment on top/left/bottom, but no right edge (the hotbar's own left edge
provides the junction), so the texture is composed on a wider canvas with the
fill continuing rightward and cropped to 64 px.
"""

from __future__ import annotations

import math
import subprocess
import sys
import tempfile
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw

REPO = Path(__file__).resolve().parent.parent
ASSETS_SRC = REPO / "assets-src"
SOIL_PATH = Path(
    "/home/mchastain/code/vs-testing/game/assets/game/textures/gui/backgrounds/soil.png"
)

# --- decompiled constants (all colors normalized to 0..1) ------------------
BASE_RGB = (64 / 255, 53 / 255, 41 / 255)  # #403529, DialogStrongBgColor, alpha 1.0
GLOW_RGB = (
    64 / 255 * 2.1,  # DialogLightBgColor[0] * 2.1
    53 / 255 * 2.1,  # DialogStrongBgColor[1] * 2.1
    41 / 255 * 2.1,  # DialogStrongBgColor[2] * 2.1
)  # (134.5, 111.3, 86.1) - the "tan" rim
BORDER_RGB = (45 / 255, 35 / 255, 33 / 255)
BORDER_ALPHA = 0.75**2  # Alpha * Alpha, Alpha = AddShadedDialogBG's 0.75
GRAIN_ALPHA = 64 / 255  # getPattern(mulAlpha: 64)
GRAIN_BLOWUP = 8  # pattern matrix 0.125 => 32px soil drawn at 256px
BLUR_SIGMA = 9  # BlurFull(scaled(9)), GUIScale = 1
CORNER_RADIUS = 1  # GuiStyle.DialogBGRadius, scaled
SS = 4  # supersampling factor so strokes get Cairo-quality antialiasing

EXT_W, EXT_H = 64, 80  # extension panel: 48px slot + 2 * 8px padding, 80px bar
VAN_W, VAN_H = 850, 80  # vanilla hotbar backdrop


def boxes_for_gauss(sigma: float, n: int = 3) -> list[int]:
    """Exact port of GaussianBlur.boxesForGauss (C# Math.Round = banker's)."""
    w_ideal = math.sqrt((12 * sigma * sigma / n) + 1)
    wl = int(math.floor(w_ideal))
    if wl % 2 == 0:
        wl -= 1
    wu = wl + 2
    m_ideal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4 * wl - 4)
    m = round(m_ideal)
    return [wl if i < m else wu for i in range(n)]


def _box_pass(rgb: np.ndarray, r: int, axis: int) -> np.ndarray:
    """Edge-clamped box average, matching fv/lv extension in the C# blur."""
    pad = [(0, 0)] * rgb.ndim
    pad[axis] = (r, r)
    padded = np.pad(rgb, pad, mode="edge")
    win = np.lib.stride_tricks.sliding_window_view(padded, 2 * r + 1, axis=axis)
    return win.sum(axis=-1) / (2 * r + 1)


def blur_full_rgb(rgb: np.ndarray, sigma: float = BLUR_SIGMA) -> np.ndarray:
    """BlurFull: 3 box passes (H then V each), RGB only, alpha untouched."""
    out = rgb
    for box in boxes_for_gauss(sigma):
        r = (box - 1) // 2
        out = _box_pass(out, r, axis=1)
        out = _box_pass(out, r, axis=0)
    return out


def over(dst: np.ndarray, src: np.ndarray) -> np.ndarray:
    """Straight-alpha source-over composite (Cairo's default Operator.Over)."""
    sa = src[..., 3:4]
    da = dst[..., 3:4]
    oa = sa + da * (1.0 - sa)
    rgb = (src[..., :3] * sa + dst[..., :3] * da * (1.0 - sa)) / np.maximum(oa, 1e-9)
    return np.concatenate([rgb, oa], axis=-1)


def _downsample(img: Image.Image, w: int, h: int) -> np.ndarray:
    img = img.resize((w, h), Image.BOX)
    return np.asarray(img, dtype=np.float64) / 255.0


def fill_layer(w: int, h: int, margin: int) -> np.ndarray:
    """Opaque rounded-rect fill: path (0,0)-(w+margin, h-1), radius 1."""
    ss = SS
    img = Image.new("RGBA", ((w + margin) * ss, h * ss), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.rounded_rectangle(
        (0, 0, (w + margin) * ss - 1, (h - 1) * ss - 1),
        radius=CORNER_RADIUS * ss,
        fill=tuple(int(round(c * 255)) for c in BASE_RGB) + (255,),
    )
    return _downsample(img, w + margin, h)


def stroke_layer(
    w: int, h: int, margin: int, expand: float, width: float, rgb: tuple, alpha: float
) -> np.ndarray:
    """Cairo-centered stroke on the path: bbox expanded by width/2 outward."""
    ss = SS
    img = Image.new("RGBA", ((w + margin) * ss, h * ss), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    e = expand * ss
    color = tuple(int(round(c * 255)) for c in rgb) + (int(round(alpha * 255)),)
    draw.rounded_rectangle(
        (-e, -e, (w + margin + expand) * ss - 1, (h - 1 + expand) * ss - 1),
        radius=int((CORNER_RADIUS + expand) * ss),
        outline=color,
        width=int(round(width * ss)),
    )
    return _downsample(img, w + margin, h)


def grain_layer(w: int, h: int, margin: int, interior_mask: np.ndarray) -> np.ndarray:
    """soil.png at 25% alpha, 8x nearest blowup, clipped to the path interior."""
    soil = Image.open(SOIL_PATH).convert("RGBA")
    tile = soil.resize((soil.width * GRAIN_BLOWUP, soil.height * GRAIN_BLOWUP), Image.NEAREST)
    tiles_x = (w + margin + tile.width - 1) // tile.width
    tiles_y = (h + tile.height - 1) // tile.height
    canvas = Image.new("RGBA", (tiles_x * tile.width, tiles_y * tile.height))
    for ty in range(tiles_y):
        for tx in range(tiles_x):
            canvas.paste(tile, (tx * tile.width, ty * tile.height))
    grain = np.asarray(canvas, dtype=np.float64)[:h, : w + margin] / 255.0
    grain[..., 3:4] = GRAIN_ALPHA
    grain[..., 3:4] *= interior_mask
    return grain


def compose(w: int, h: int, open_right: bool = False) -> np.ndarray:
    """Full vanilla pipeline. open_right=True composes the left end of a
    continuous bar (no right-edge rim) for the extension panel."""
    margin = 48 if open_right else 0

    fill = fill_layer(w, h, margin)
    glow = stroke_layer(
        w, h, margin, expand=5.0, width=10.0, rgb=GLOW_RGB, alpha=1.0  # strokeWidth*2
    )
    base = over(fill, glow)

    # BlurFull blurs the premultiplied ARGB32 buffer; alpha passes through.
    premult = base[..., :3] * base[..., 3:4]
    blurred = blur_full_rgb(premult)
    base = np.concatenate(
        [blurred / np.maximum(base[..., 3:4], 1e-9), base[..., 3:4]], axis=-1
    )

    # Grain fills the path interior only (FillPreserve after the blur).
    interior = (fill[..., 3] > 0.5).astype(np.float64)[..., None]
    base = over(base, grain_layer(w, h, margin, interior))

    # Border: rgba(45,35,33,0.5625), width strokeWidth = 5.
    border = stroke_layer(
        w, h, margin, expand=2.5, width=5.0, rgb=BORDER_RGB, alpha=BORDER_ALPHA
    )
    base = over(base, border)

    if open_right:
        base = base[:, :w]
    return base


def save_png(path: Path, arr: np.ndarray) -> None:
    Image.fromarray(np.round(arr * 255).astype(np.uint8), "RGBA").save(path)


def layer_pngs(arr: np.ndarray, fill_glow: np.ndarray, grain: np.ndarray, interior: np.ndarray,
               border: np.ndarray, outdir: Path, prefix: str) -> list[Path]:
    """Write the three XCF layer images (bottom -> top)."""
    paths = []
    specs = [
        (f"{prefix}-1-fill-glow-blurred.png", fill_glow),
        (f"{prefix}-2-soil-grain-25pct.png",
         np.concatenate([grain[..., :3], grain[..., 3:4] * interior], axis=-1)),
        (f"{prefix}-3-border-stroke.png", border),
    ]
    for name, layer in specs:
        p = outdir / name
        save_png(p, layer)
        paths.append(p)
    return paths


XCF_SCRIPT = """
(let* ((image (car (gimp-file-load RUN-NONINTERACTIVE "{bottom}" "{bottom}"))))
  (gimp-item-set-name (vector-ref (car (gimp-image-get-layers image)) 0) "{bottom_name}")
  (let ((grain-layer (car (gimp-layer-new-from-drawable
                           (car (gimp-file-load RUN-NONINTERACTIVE "{mid}" "{mid}")) image))))
    (gimp-image-insert-layer image grain-layer 0 0)
    (gimp-item-set-name grain-layer "{mid_name}"))
  (let ((border-layer (car (gimp-layer-new-from-drawable
                            (car (gimp-file-load RUN-NONINTERACTIVE "{top}" "{top}")) image))))
    (gimp-image-insert-layer image border-layer 0 0)
    (gimp-item-set-name border-layer "{top_name}"))
  (gimp-file-save RUN-NONINTERACTIVE image "{out}")
  (gimp-quit 0))
"""


def build_xcf(layers: list[Path], out_path: Path, names: list[str]) -> None:
    bottom, mid, top = layers
    script = XCF_SCRIPT.format(
        bottom=bottom, mid=mid, top=top, out=out_path,
        bottom_name=names[0], mid_name=names[1], top_name=names[2],
    )
    result = subprocess.run(
        ["gimp", "-i", "-d", "-f", "--batch-interpreter", "plug-in-script-fu-eval",
         "-b", script, "-b", "(gimp-quit 0)"],
        capture_output=True, text=True, timeout=300,
    )
    if result.returncode != 0:
        sys.exit(f"gimp batch failed:\n{result.stdout}\n{result.stderr}")


def profile(arr: np.ndarray, column: int, rows: int = 30) -> list[tuple[int, int, int, int]]:
    return [
        (y, *tuple(int(v * 255) for v in arr[y, column][:3])) for y in range(rows)
    ]


def report(arr: np.ndarray, label: str, column: int) -> None:
    print(f"\n{label} - top-edge rim profile (column {column}):")
    for y, r, g, b in profile(arr, column):
        print(f"  row {y:3d}: ({r:3d},{g:3d},{b:3d})")
    rgb = arr[..., :3] * 255
    base = rgb[40, column - 5]
    dev = np.abs(rgb[:30, column] - base).sum(axis=-1)
    peak = int(np.argmax(dev))
    settle = next((y for y in range(peak, 30) if dev[y] < 18), -1)
    print(f"  peak at row {peak} {tuple(int(v) for v in rgb[peak, column])}, "
          f"settles by row {settle}, interior base {tuple(int(v) for v in base)}")


def main() -> None:
    # --- extension panel: left end of a continuous bar, open right --------
    ext = compose(EXT_W, EXT_H, open_right=True)
    save_png(REPO / "assets/openhand/textures/hud/hotbar-extension.png", ext)
    save_png(ASSETS_SRC / "hotbar-extension-background.png", ext)

    # --- vanilla bar ------------------------------------------------------
    van = compose(VAN_W, VAN_H)
    save_png(ASSETS_SRC / "vanilla-hotbar-background.png", van)
    preview = Image.fromarray(np.round(van * 255).astype(np.uint8), "RGBA")
    preview.resize((VAN_W * 2, VAN_H * 2), Image.NEAREST).save(
        ASSETS_SRC / "vanilla-hotbar-background-preview-2x.png"
    )

    # --- layered XCFs -----------------------------------------------------
    with tempfile.TemporaryDirectory() as tmp:
        tmp = Path(tmp)
        # Recompose per-layer for the XCFs (same code path, captured stages).
        for w, h, open_right, xcf, prefix in [
            (EXT_W, EXT_H, True, ASSETS_SRC / "hotbar-extension-background.xcf", "ext"),
            (VAN_W, VAN_H, False, ASSETS_SRC / "vanilla-hotbar-background.xcf", "van"),
        ]:
            margin = 48 if open_right else 0
            fill = fill_layer(w, h, margin)
            glow = stroke_layer(w, h, margin, 5.0, 10.0, GLOW_RGB, 1.0)
            fill_glow = over(fill, glow)
            blurred = blur_full_rgb(fill_glow[..., :3] * fill_glow[..., 3:4])
            bottom = np.concatenate(
                [blurred / np.maximum(fill_glow[..., 3:4], 1e-9), fill_glow[..., 3:4]],
                axis=-1,
            )
            interior = (fill[..., 3] > 0.5).astype(np.float64)[..., None]
            grain = grain_layer(w, h, margin, interior)
            border = stroke_layer(w, h, margin, 2.5, 5.0, BORDER_RGB, BORDER_ALPHA)
            layers = layer_pngs(None, fill_glow, grain, interior, border, tmp, prefix)
            if open_right:
                layers = []
                for p in [tmp / f"{prefix}-1-fill-glow-blurred.png",
                          tmp / f"{prefix}-2-soil-grain-25pct.png",
                          tmp / f"{prefix}-3-border-stroke.png"]:
                    img = np.asarray(Image.open(p))[:, :w] / 255.0
                    out = tmp / (p.stem + "-crop.png")
                    save_png(out, img)
                    layers.append(out)
            build_xcf(
                layers, xcf,
                ["fill + glow, BlurFull(9) baked", "soil grain 25%", "border stroke"],
            )

    # --- diagnostics ------------------------------------------------------
    for arr, label, col in [
        (van, "vanilla 850x80", VAN_W // 2),
        (ext, "extension 64x80", EXT_W // 2),
    ]:
        report(arr, label, col)

    print("\nboxesForGauss(9, 3) =", boxes_for_gauss(BLUR_SIGMA), "(radii",
          [(b - 1) // 2 for b in boxes_for_gauss(BLUR_SIGMA)], ")")
    print("wrote extension texture, vanilla/extension design PNGs and XCFs")


if __name__ == "__main__":
    main()
