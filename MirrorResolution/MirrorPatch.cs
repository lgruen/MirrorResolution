using HarmonyLib;
using UnityEngine;
using UnityEngine.XR;

namespace MirrorResolution
{
    // MirrorRendererSO renders the stereo reflection side by side into a texture of the preset's fixed
    // size (High: 2400x1000, Medium: 1200x500). The mirror camera uses each eye's own projection, so a
    // reflection texel maps 1:1 to a screen pixel; matching the eye texture size gives a full-res mirror.
    [HarmonyPatch(typeof(MirrorRendererSO), nameof(MirrorRendererSO.RenderMirrorTexture))]
    internal static class MirrorPatch
    {
        private static int _gameAntiAliasing = -1;
        private static int _loggedWidth;
        private static int _loggedHeight;
        private static int _loggedAa;
        private static bool _loggedSplit;

        private static void Prefix(Camera currentCamera, ref int ____stereoTextureWidth, ref int ____stereoTextureHeight, ref int ____antialiasing)
        {
            var config = PluginConfig.Instance;
            // Off/Low presets use 2x2 placeholders; leave those alone.
            if (_gameAntiAliasing >= 0 && !config.Enabled)
            {
                ____antialiasing = _gameAntiAliasing;
            }

            if (!config.Enabled || currentCamera == null || !currentCamera.stereoEnabled || ____stereoTextureHeight <= 2)
            {
                return;
            }

            int eyeWidth = XRSettings.eyeTextureWidth;
            int eyeHeight = XRSettings.eyeTextureHeight;
            if (eyeWidth <= 0 || eyeHeight <= 0)
            {
                eyeWidth = currentCamera.pixelWidth;
                eyeHeight = currentCamera.pixelHeight;
            }

            float scale = Mathf.Clamp(config.Scale, 0.1f, 2f);
            int perEyeWidth = Mathf.RoundToInt(eyeWidth * scale);
            int perEyeHeight = Mathf.RoundToInt(eyeHeight * scale);
            int maxPerEye = config.MaxPerEye;
            if (maxPerEye > 0 && Mathf.Max(perEyeWidth, perEyeHeight) > maxPerEye)
            {
                float shrink = (float)maxPerEye / Mathf.Max(perEyeWidth, perEyeHeight);
                perEyeWidth = Mathf.RoundToInt(perEyeWidth * shrink);
                perEyeHeight = Mathf.RoundToInt(perEyeHeight * shrink);
            }

            int width = 2 * Mathf.Max(1, perEyeWidth);
            int height = Mathf.Max(1, perEyeHeight);
            ____stereoTextureWidth = width;
            ____stereoTextureHeight = height;

            // _antialiasing is only computed in Awake; remember the game's value so it can be restored.
            if (_gameAntiAliasing < 0)
            {
                _gameAntiAliasing = ____antialiasing;
            }

            int aa = config.AntiAliasing;
            int mirrorAa = aa == 1 || aa == 2 || aa == 4 || aa == 8 ? aa : _gameAntiAliasing;
            MirrorSplit.EyeAntiAliasing = mirrorAa;
            ____antialiasing = config.SplitEyes ? 1 : mirrorAa;

            if (width != _loggedWidth || height != _loggedHeight || mirrorAa != _loggedAa || config.SplitEyes != _loggedSplit)
            {
                _loggedAa = mirrorAa;
                _loggedSplit = config.SplitEyes;
                _loggedWidth = width;
                _loggedHeight = height;
                Plugin.Log.Info($"Mirror texture {width}x{height} ({width / 2}x{height} per eye, eye texture {eyeWidth}x{eyeHeight}, scale {scale}, max per eye {maxPerEye}, MSAA {mirrorAa}, split eyes {config.SplitEyes})");
            }
        }
    }
}
