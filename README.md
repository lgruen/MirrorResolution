# MirrorResolution

A BSIPA mod for Beat Saber 1.44 (PC) that renders the reflective floor ("Mirror: Medium/High") at the
headset's eye resolution instead of the game's fixed size.

The game renders the stereo reflection into a side-by-side texture whose size comes from the quality
preset: **High 2400×1000 (1200×1000 per eye), Medium 1200×500**, with at most 2× MSAA, regardless of
headset or render scale. The mirror camera uses each eye's own projection, so a reflection texel maps
1:1 to a screen pixel; a modern headset's eye buffer (2000–2700 px) is about twice that size per axis.

## Settings (`UserData/MirrorResolution.json`, reloaded live)

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | `false` restores the game's behaviour |
| `Scale` | `1.0` | mirror size per eye as a fraction of the eye texture |
| `MaxPerEye` | `0` | cap for the per-eye width/height in pixels (0 = none); keeps the cost fixed when the eye resolution changes |
| `AntiAliasing` | `0` | mirror MSAA samples (1, 2, 4, 8); 0 keeps the game's |
| `SplitEyes` | `true` | render each eye's reflection into its own texture and copy it into its half (see below) |
| `ClipToMirror` | `true` | render only the screen rectangle the mirrors cover (needs `SplitEyes`) |
| `FootprintMask` | `true` | within that, shade only the pixels the mirrors cover |

Off and Low (the "fake mirror") are not affected.

## SplitEyes

The game renders the two eyes as two camera renders with half-width viewports into the shared texture.
On a tiled GPU each render is a pass over the whole double-wide surface (load, store, binning), so the
mirror costs about twice what it should, more with MSAA. With `SplitEyes` each eye renders into a texture
of its own size, is resolved, and is copied into its half. On desktop GPUs expect little difference.

Steam Frame (Adreno 750, Turnip; 2736² eye, mirror 1620² per eye), game GPU ms/frame:

| | The Sun 0:30–1:00 | A Cookie From Space E+ 1:00–1:30 |
|---|---|---|
| unsplit, MSAA 2× | 9.63 | |
| SplitEyes, MSAA 2× (0.3.1) | 7.69 / 7.66 | 20.32 / 20.41 |
| SplitEyes, MSAA 1× | 7.27 | 18.63 |
| SplitEyes, MSAA 2× (0.3.0, black reflection, see below) | 7.12 | 19.86 |

0.3.0 resolved the multisampled eye texture with `ResolveAntiAliasedSurface(other)`, which leaves the other
texture black on this setup (Proton/DXVK): with MSAA 2× the reflection was empty. 0.3.1 resolves with a blit.

## ClipToMirror and FootprintMask

The floor samples the reflection at its own screen position, so only the part of each eye's reflection
that the mirrors cover is ever seen; the game renders all of it. Each frame the mod projects the bounds of
every active `Mirror` into each eye (clipped at the eye plane) and takes the convex hull, grown by a margin
of 0.05 (normalized device coordinates) for the shader's normal-map offset.

- `ClipToMirror` renders the hull's bounding rectangle into a texture of that size (rounded up to 128 px);
  no mirror in view, no render. The projection stays the eye's own and the viewport keeps the eye's size,
  shifted so the rectangle lands on the texture: an off-centre projection gives the same geometry but moves
  the screen position the game's shaders use for the bloom fog lookup.
- `FootprintMask` gives the rest of the rectangle the near depth before the scene draws (a command buffer
  draws the outside of the hull as quads at z = near, same depth test direction as the scene), so the scene
  there fails the early depth test and isn't shaded. It writes near outside rather than far inside because
  Turnip disables LRZ for the pass when a depth-writing draw changes direction or depth is cleared inside it
  (traced: the far-inside variant ends the pass with LRZ off, "Depth write + ALWAYS/NOT_EQUAL").

Steam Frame, same settings (SplitEyes, MSAA 2×, Chroma 2.9.23), game GPU ms/frame, two runs each:

| | The Sun 0:30–1:00 | A Cookie From Space E+ 1:00–1:30 |
|---|---|---|
| 0.3.1 | 7.62 / 7.61 | 20.24 / 20.27 (68 % reprojected) |
| `ClipToMirror` | 7.15 / 7.19 | 13.38 / 13.40 (5 %) |
| `ClipToMirror` + `FootprintMask` | 7.01 / 7.01 | 13.35 / 13.36 (4–5 %) |
| `FootprintMask` alone | 7.17 / 7.23 | 19.61 / 19.66 |

Head forward: on The Sun the rectangle is 63 % of each eye and the footprint 39–40 %; on A Cookie From
Space the only mirror is the platform under the player, so the rectangle is 1620×128. In a Turnip trace the
mirror pass keeps its GMEM bins (all of them cross the footprint) and LRZ writes are already off in it
(DXVK records it in a secondary command buffer, with a blended depth-writing draw), so the mask saves
fragment shading through the per-pixel early depth test only.

Output, compared with the plain render of the same frame (`MIRROR_DUMP`): `FootprintMask` alone is
identical inside the footprint. With `ClipToMirror` the game's dither pattern follows the texture size and
differs (median 2/255, 99.9 % of pixels within 13/255, max 22/255 in the reflection texture).

`MIRROR_DUMP=<seconds>` (environment) saves the reflection textures `<seconds>` into a level as
`bench_shot_*.png` in the game folder, with the same frame rendered without clipping and mask
(`bench_shot_ref_[lr].png`) and the rectangle and footprint in the log. `MIRROR_MASK_COLOR=1` paints the
masked area magenta; `MIRROR_MARGIN=<ndc>` overrides the margin; `MIRROR_MASK=far` is the clear-near,
write-far-inside variant (for traces).

## Build

```sh
dotnet build MirrorResolution/MirrorResolution.csproj -c Release -p:BeatSaberDir=<Beat Saber folder>
```

Copy `MirrorResolution.dll` to `Plugins`. Needs BSIPA only.
