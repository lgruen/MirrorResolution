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
| `SplitEyes` | `false` | render each eye's reflection into its own texture and copy it into its half (see below) |

Off and Low (the "fake mirror") are not affected.

## SplitEyes

The game renders the two eyes as two camera renders with half-width viewports into the shared texture.
On a tiled GPU each render is a pass over the whole double-wide surface (load, store, binning), so the
mirror costs about twice what it should, more with MSAA. With `SplitEyes` each eye renders into a texture
of its own size, is resolved, and is copied into its half. On a Steam Frame (Adreno 750, Turnip; 2736² eye,
mirror 1620² per eye, 2× MSAA) the game's GPU time went from 9.63 to 7.11 ms per frame. On desktop GPUs
expect little difference.

## Build

```sh
dotnet build MirrorResolution/MirrorResolution.csproj -c Release -p:BeatSaberDir=<Beat Saber folder>
```

Copy `MirrorResolution.dll` to `Plugins`. Needs BSIPA only.
