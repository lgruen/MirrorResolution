# MirrorResolution

A BSIPA mod for Beat Saber 1.44.1, made for **[bs-arm64](https://github.com/DaVarga/bs-arm64) on the
Steam Frame** (Snapdragon 8 Gen 3, Adreno 750 with Mesa's Turnip driver, a tiled GPU). It renders the
floor mirror (graphics setting "Mirror: Medium/High") at a resolution you choose instead of the game's
fixed size, and makes it much cheaper on a tiled GPU: each eye gets its own render, and only the part of
the reflection the mirrors cover is drawn.

Tested only on the Steam Frame. The mod is plain Unity and should load on a PC, but its savings come
from how tiled mobile GPUs work. On a desktop GPU expect the resolution change to cost what it costs and
`SplitEyes`, `ClipToMirror` and `FootprintMask` to save little or nothing; that hasn't been measured.

## Install

Copy `Plugins/MirrorResolution.dll` from the release zip into the game's `Plugins` folder. Needs BSIPA
only. Set the in-game mirror to Medium or High; Off and Low (the "fake mirror") are left alone.

## Settings (`UserData/MirrorResolution.json`, reloaded while the game runs)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false` restores the game's behaviour |
| `Scale` | `0.75` | mirror size per eye as a fraction of the eye texture |
| `MaxPerEye` | `1620` | cap for the per-eye width and height in pixels (0 = none); keeps the cost fixed when the eye resolution changes |
| `AntiAliasing` | `0` | mirror MSAA samples (1, 2, 4, 8); 0 keeps the game's (2× on Medium/High) |
| `SplitEyes` | `true` | render each eye's reflection into a texture of its own (see below) |
| `ClipToMirror` | `true` | render only the screen rectangle the mirrors cover (needs `SplitEyes`) |
| `FootprintMask` | `true` | within that rectangle, shade only the pixels the mirrors cover |

The game's sizes are High 1200×1000 per eye and Medium 600×500, whatever the headset. With the mod both
get the size above; Medium still reflects less (it leaves out the environment layer, which is what makes
it cheap on maps with heavy Chroma geometry).

## Why the reflection can match the screen

The mirror camera uses each eye's own projection, reflected in the floor plane, and the floor samples the
reflection texture at its own screen position. So one reflection texel lands on one screen pixel: the
reflection is a second picture of the eye's view, and its natural size is the eye texture's. The game's
1200×1000 is about half of that per axis on a current headset (2736² or more per eye on the Frame).

## How it saves GPU time

**SplitEyes.** The game renders the stereo reflection as two camera renders into one side-by-side texture,
each with a half-width viewport. On a tiled GPU each of those is a render pass over the whole double-wide
surface: binning, loading and storing every tile, twice, more with MSAA. With `SplitEyes` each eye renders
into a texture of exactly its size, which is resolved and copied into its half.
The resolve is a blit: `RenderTexture.ResolveAntiAliasedSurface` left the destination black on the Frame
(DXVK), which made 0.3.0's reflection empty with MSAA.

**ClipToMirror.** Only the part of each eye's reflection that the mirrors cover is ever seen, but the game
renders all of it. Each frame the mod projects the bounds of every active `Mirror` into each eye (clipped
at the eye plane, so the platform under the player works), takes the convex hull and grows it by 0.05
(normalized device coordinates) for the shader's normal-map offset. The hull's bounding rectangle is
rendered into a texture of that size (rounded up to 128 px); with no mirror in view nothing is rendered.
The projection stays the eye's own and the viewport keeps the eye's size, shifted so the rectangle lands on
the texture, so every pixel is computed exactly as in the full render. Shader inputs that depend on the
render size are set for the eye's size: `_ScreenParams` and the game's blue-noise dither tiling.

**FootprintMask.** Before the scene draws, the part of the rectangle outside the hull gets the near
depth (a few quads, drawn with the scene's own depth test direction), so the scene fails the early depth
test there and isn't shaded. It writes near outside rather than far inside because Turnip turns off LRZ
for the rest of a pass when a depth-writing draw changes the compare direction or depth is cleared inside
it.

## Measurements (Steam Frame)

Game GPU time per frame from the kernel's DRM counters (SteamVR's app GPU time only covers part of the
frame here), headset still with the head pinned looking forward, 30 s of a song, Valve's foveated
rendering default, game MSAA 2×, mirror High at 1620² per eye, two runs each unless noted.

bs-arm64 0.2.2, 2736² per eye, 72 Hz (budget 13.9 ms):

| | The Sun 0:30–1:00 | A Cookie From Space E+ 1:00–1:30 (Chroma) |
|---|---|---|
| one side-by-side texture as the game renders it (`SplitEyes` off), bs-arm64 0.2.1 | 9.63 / 9.63 | |
| `SplitEyes` (0.3.1) | 7.62 / 7.61 | 20.24 / 20.27 (68 % reprojected) |
| + `ClipToMirror` | 7.15 / 7.19 | 13.38 / 13.40 (5 %) |
| + `ClipToMirror` + `FootprintMask` (0.4) | 7.01 / 7.01 | 13.35 / 13.36 (4–5 %) |
| `FootprintMask` alone | 7.17 / 7.23 | 19.61 / 19.66 |

Earlier numbers for 0.3.0 (The Sun 7.11–7.12 ms with `SplitEyes`) were measured with an empty (black)
reflection, see above; they are not comparable.

Looking forward on The Sun the rectangle is 63 % of each eye and the footprint 39–40 %; on A Cookie From
Space only the platform's mirror is in view, so the rectangle is 1620×128 and most of the saving there is
the map's Chroma geometry that no longer gets drawn into the reflection. The mask's own gain is small: the
mirror pass keeps all its GMEM bins (each crosses the footprint), and LRZ writes are already off in it, so
it saves fragment shading through the per-pixel early depth test only.

bs-arm64 0.2.2, 3408² per eye (SteamVR resolution 3420), 90 Hz (budget 11.1 ms), one run each, same
settings otherwise:

| Environment (map, song time) | Mirrors | Rectangle / footprint per eye | `ClipToMirror` + `FootprintMask` off | on |
|---|---|---|---|---|
| BigMirror (Bark, 0:30) | floor, platform | 63 % / 54–56 % | 9.78 | 9.14 |
| Triangle (Komodo, 0:30) | track, platform | 63 % / 39–40 % | 11.45 | 10.35 |
| Default (The Sun, 0:30) | track, platform | 63 % / 39–40 % | | 9.79 |
| Nice (Saeed, 0:30) | platform | 8 % / 2.5–4.4 % | | 8.79 |
| Weave (Magic, 0:30) | platform | 8 % / 2.5–4.4 % | | 5.84 |
| BTS + Chroma (A Cookie From Space, 1:00) | platform | 8 % / 2.5–4.4 % | | 16.03 (67 % reprojected) |

The Sun with the mirror set to Medium: 9.17, Low: 8.25, Off: 8.11.

## Output

`MIRROR_DUMP` (below) renders the same frame twice, once as the plain per-eye render and once clipped and
masked. Inside the mirrors' footprint the two are pixel-identical on The Sun, BigMirror, Triangle, Nice and
Weave, also with game MSAA 0/4/8, mirror MSAA 4 (8: a few pixels differ by up to 4/255), mirror Medium and
looking 30° down. Off and Low render no reflection and are untouched.

One known difference: on A Cookie From Space about 1 % of the footprint's pixels (thin dark strokes of the
map's geometry in the platform's reflection) differ by up to 15/255. They come from the culling: the
mirror camera culls with the rectangle, and some of the map's objects that this skips still show up inside
it (their renderer bounds apparently don't cover everything they draw; growing the rectangle by 0.1 for
culling didn't help). Culling with the whole eye makes the output identical there, but costs 2.7 ms per
frame on that map.

What the headset gets: with the level frozen (same frame, same dither), the final eye images (3408² each)
are identical with and without `ClipToMirror` and `FootprintMask` on all six environments, looking 30°
down, and with `SplitEyes` off (mask only); on A Cookie From Space 0.0006 % of one eye's pixels differ by
1/255. (The internal testing switch `MirrorSplit.Plain` turns both off for such A/B shots.)

Before 0.4.1 the clipped render tiled the game's blue-noise dither over the clipped texture instead of the
eye, which stretched it by eye size / texture size (mean difference 2–4/255, vertical streaks when only
the platform's mirror was in view).

## Debugging

Environment variables (for the game process):

- `MIRROR_DEBUG=1` logs the scene's `Mirror` components and, for a few frames, every mirror texture the
  game renders (camera, eye, plane, rendered or cached).
- `MIRROR_DUMP=<seconds>` saves, that long into a level, the reflection textures as `bench_shot_*.png` in
  the game folder: the mod's per-eye textures (`split_[lr]`), the same frame rendered without clipping and
  mask (`ref_[lr]`) and the final side-by-side texture (`mirror`), and logs the rectangle and the
  footprint polygons (`MIRRORDUMP`).
- `MIRROR_MASK_COLOR=1` paints the masked area magenta; `MIRROR_MARGIN=<ndc>` overrides the margin;
  `MIRROR_MASK=far` is the clear-near, write-far-inside mask variant (for comparing GPU traces).

## Build

```sh
dotnet build MirrorResolution/MirrorResolution.csproj -c Release -p:BeatSaberDir=<Beat Saber folder>
```

The game folder provides the referenced assemblies (`Beat Saber_Data/Managed`, `Libs/0Harmony.dll`); none
are in this repository. Output: `MirrorResolution/bin/Release/net472/MirrorResolution.dll`.

## License

MIT, see [LICENSE](LICENSE).
