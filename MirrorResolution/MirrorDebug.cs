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

        // MIRROR_DUMP=<seconds>: once, that long after a level started (song time), save the stereo reflection texture as
        // bench_shot_mirror.png in the game folder (the benchmark's run.sh collects bench_shot_*.png).
        private static readonly float DumpAfter = float.TryParse(Environment.GetEnvironmentVariable("MIRROR_DUMP"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float s) ? s : -1f;
        private static bool _dumped;
        private static AudioTimeSyncController? _audio;
        private static float _nextLookup;
        private static int _loggedFrames;
        private static int _lastFrame = -1;

        // True during the RenderMirrorTexture call whose textures are being dumped.
        internal static bool Dumping;
        private static int _logCalls = 12;

        private static void Prefix(Camera currentCamera, object ____renderTextures, out int __state)
        {
            if (DumpAfter >= 0f && LevelTime() >= DumpAfter && _logCalls-- > 0 && currentCamera != null)
            {
                Plugin.Log.Info($"MIRRORDBG frame {Time.frameCount} cam '{currentCamera.name}' stereo={currentCamera.stereoEnabled} mask={currentCamera.cullingMask:X} " +
                                $"target={currentCamera.targetTexture?.name} pos={currentCamera.transform.position} depth={currentCamera.depth}");
            }

            Dumping = DumpAfter >= 0f && !_dumped && currentCamera != null && currentCamera.stereoEnabled && LevelTime() >= DumpAfter + 0.5f;
            if (Dumping)
            {
                Capture(Texture2D.whiteTexture, "white");
                var bloom = Shader.GetGlobalTexture("_BloomPrePassTexture");
                if (bloom != null)
                {
                    Capture(bloom, "bloom");
                }
            }

            __state = Enabled ? ((System.Collections.ICollection)____renderTextures).Count : 0;
        }

        private static void Postfix(Camera currentCamera, Vector3 reflectionPlanePos, Vector3 reflectionPlaneNormal,
            object ____renderTextures, int __state, Texture __result)
        {
            if (Dumping)
            {
                Dumping = false;
                _dumped = true;
                if (__result != null)
                {
                    Capture(__result, "mirror");
                }
            }

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

        private static float LevelTime()
        {
            if (_audio == null && Time.time >= _nextLookup)
            {
                _nextLookup = Time.time + 0.5f;
                _audio = UnityEngine.Object.FindAnyObjectByType<AudioTimeSyncController>();
            }

            return _audio == null ? -1f : _audio.songTime - _audio.startSongTime;
        }

        // Logs the rendered rectangle and the footprint polygons (grown by the margin, and exact) in pixels of the
        // eye's full reflection (origin bottom left).
        internal static void LogFootprint(string eye, int px0, int py0, int px1, int py1, int width, int height,
            System.Collections.Generic.List<Vector2> grown, System.Collections.Generic.List<Vector2> exact)
        {
            string Pixels(System.Collections.Generic.List<Vector2> polygon) => string.Join(" ", polygon.ConvertAll(p =>
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:F1},{1:F1}", (p.x + 1f) * 0.5f * width, (p.y + 1f) * 0.5f * height)));
            Plugin.Log.Info($"MIRRORDUMP eye={eye} size={width}x{height} rect={px0},{py0},{px1},{py1} grown={Pixels(grown)} exact={Pixels(exact)}");
        }

        // Copies a texture (or one layer of an array) now and saves it as bench_shot_<name>.png once read back;
        // logs the mean colour. Sources must not be multisampled.
        internal static void Capture(Texture source, string name, int element = 0)
        {
            bool array = source is RenderTexture { dimension: UnityEngine.Rendering.TextureDimension.Tex2DArray };
            var copy = array
                ? new RenderTexture(source.width, source.height, 0, source.graphicsFormat)
                : new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            copy.Create();
            if (array)
            {
                Graphics.CopyTexture(source, element, 0, copy, 0, 0);
            }
            else
            {
                Graphics.Blit(source, copy);
            }

            int frame = Time.frameCount;
            string path = $"bench_shot_{name}.png";
            UnityEngine.Rendering.AsyncGPUReadback.Request(copy, 0, request =>
            {
                if (request.hasError)
                {
                    Plugin.Log.Warn($"Readback of {name} failed");
                }
                else
                {
                    var data = request.GetData<byte>();
                    double sum = 0;
                    for (int i = 0; i < data.Length; i += 4)
                    {
                        sum += data[i] + data[i + 1] + data[i + 2];
                    }

                    var png = ImageConversion.EncodeNativeArrayToPNG(data, copy.graphicsFormat, (uint)copy.width, (uint)copy.height);
                    System.IO.File.WriteAllBytes(path, png.ToArray());
                    png.Dispose();
                    Plugin.Log.Info($"Saved {path} ({copy.width}x{copy.height}, frame {frame}, mean RGB sum {sum / (data.Length / 4):F2})");
                }

                copy.Release();
                UnityEngine.Object.Destroy(copy);
            });
        }
    }
}
