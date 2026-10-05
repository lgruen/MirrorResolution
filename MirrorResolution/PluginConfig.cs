using System.Runtime.CompilerServices;
using IPA.Config.Stores;

[assembly: InternalsVisibleTo(GeneratedStore.AssemblyVisibilityTarget)]

namespace MirrorResolution
{
    internal class PluginConfig
    {
        public static PluginConfig Instance { get; set; } = null!;

        // Off: leave the game's preset size untouched.
        public virtual bool Enabled { get; set; } = true;

        // Mirror size per eye as a fraction of the eye texture size (1.0 = same as the eye buffer).
        public virtual float Scale { get; set; } = 0.75f;

        // Upper limit for the mirror's per-eye width and height in pixels; 0 = no limit. Keeps the
        // mirror's cost fixed when the eye resolution is raised.
        public virtual int MaxPerEye { get; set; } = 1620;

        // MSAA samples for the mirror (1, 2, 4, 8); 0 keeps the game's value (2 on Medium/High).
        public virtual int AntiAliasing { get; set; } = 0;

        // Render each eye's reflection into its own texture instead of half of a shared one (fewer
        // full-surface tile loads/stores on tiled GPUs).
        public virtual bool SplitEyes { get; set; } = true;

        // Render only the rectangle of each eye's reflection that the mirrors cover on screen, into a texture of
        // that size (nothing when no mirror is in view). Needs SplitEyes. Same image apart from the game's dither
        // pattern, which follows the texture size.
        public virtual bool ClipToMirror { get; set; } = true;

        // Within that, keep the scene out of the pixels outside the mirrors' footprint (depth-masked before the
        // reflection renders, so they are never shaded). Identical pixels where the mirror is.
        public virtual bool FootprintMask { get; set; } = true;
    }
}
