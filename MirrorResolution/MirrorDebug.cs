using System;
using HarmonyLib;
using UnityEngine;

namespace MirrorResolution
{
    // MIRROR_DEBUG=1: log, for a few frames, every mirror texture the game renders (camera, stereo, plane).
    [HarmonyPatch(typeof(MirrorRendererSO), nameof(MirrorRendererSO.RenderMirrorTexture))]
    internal static class MirrorDebug
    {
        private static readonly bool Enabled = Environment.GetEnvironmentVariable("MIRROR_DEBUG") == "1";
        private static int _loggedFrames;
        private static int _lastFrame = -1;

        private static void Prefix(object ____renderTextures, out int __state)
        {
            __state = Enabled ? ((System.Collections.ICollection)____renderTextures).Count : 0;
        }

        private static void Postfix(Camera currentCamera, Vector3 reflectionPlanePos, Vector3 reflectionPlaneNormal,
            object ____renderTextures, int __state, Texture __result)
        {
            if (!Enabled || _loggedFrames > 6 || Time.timeSinceLevelLoad < 5)
            {
                return;
            }

            if (Time.frameCount != _lastFrame)
            {
                _lastFrame = Time.frameCount;
                _loggedFrames++;
            }

            bool rendered = ((System.Collections.ICollection)____renderTextures).Count > __state;
            Plugin.Log.Info($"MIRRORDBG frame {Time.frameCount} cam '{currentCamera?.name}' stereo={currentCamera?.stereoEnabled} " +
                            $"eye={currentCamera?.stereoTargetEye} pos={currentCamera?.transform.position} plane={reflectionPlanePos}/{reflectionPlaneNormal} " +
                            $"{(rendered ? "RENDERED" : "cached")} tex={__result?.width}x{__result?.height}");
        }
    }
}
