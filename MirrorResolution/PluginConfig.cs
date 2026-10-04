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
        public virtual float Scale { get; set; } = 1.0f;

        // Upper limit for the mirror's per-eye width and height in pixels; 0 = no limit. Keeps the
        // mirror's cost fixed when the eye resolution is raised.
        public virtual int MaxPerEye { get; set; } = 0;

        // MSAA samples for the mirror (1, 2, 4, 8); 0 keeps the game's value (2 on Medium/High).
        public virtual int AntiAliasing { get; set; } = 0;

        // Render each eye's reflection into its own texture instead of half of a shared one (fewer
        // full-surface tile loads/stores on tiled GPUs). Experimental.
        public virtual bool SplitEyes { get; set; } = false;

        // Render only the part of each eye's reflection that the mirror covers on screen (nothing when the
        // floor mirror is out of view). Same pixels where the mirror is. Experimental.
        public virtual bool ClipToMirror { get; set; } = false;

        // Within that, keep the scene out of the pixels outside the mirrors' footprint (depth-masked before the
        // reflection renders, so they are never shaded). Same pixels where the mirror is. Experimental.
        public virtual bool FootprintMask { get; set; } = false;
    }
}
