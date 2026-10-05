using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MirrorResolution
{
    // The game renders the stereo mirror as two camera renders into one side-by-side texture, each with a
    // half-width viewport. On the Frame's tiled GPU each of those passes still loads and stores the whole
    // double-wide surface and bins every tile. With SplitEyes, each eye renders into its own texture of
    // exactly its size, which is then resolved (MSAA) and copied into its half of the side-by-side texture.
    // With ClipToMirror (MirrorClip) only the bounding rectangle of the mirrors' footprint is rendered, into a
    // texture of that size; with FootprintMask (MirrorMask) the scene is kept out of the rest of it.
    [HarmonyPatch(typeof(MirrorRendererSO), "RenderMirror")]
    internal static class MirrorSplit
    {
        // MSAA samples for the per-eye textures; the side-by-side texture is single-sampled when splitting.
        internal static int EyeAntiAliasing = 1;

        // Grows the mirrors' footprint on each side (normalized device coordinates, 2 = the eye's width): the mirror
        // shader offsets where it samples the reflection by its normal map. MIRROR_MARGIN overrides it (testing).
        private static readonly float Margin = float.TryParse(Environment.GetEnvironmentVariable("MIRROR_MARGIN"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float m) ? m : 0.05f;

        private static readonly List<Vector2> Hull = new List<Vector2>();

        // Testing: true renders as with ClipToMirror and FootprintMask off (for A/B screenshots of the whole view, e.g.
        // the bench harness's BENCH_SHOT_AB=<s>,MirrorResolution.MirrorSplit:Plain,<frames>,freeze).
        internal static bool Plain;

        private static bool Prefix(Vector3 __0, Quaternion __1, Matrix4x4 __2, Rect __3, Vector3 __4, Vector3 __5, Camera ____mirrorCamera)
        {
            var config = PluginConfig.Instance;
            Rect screenRect = __3;
            var camera = ____mirrorCamera;
            RenderTexture? target = camera.targetTexture;
            MirrorMask.Clear(camera);
            if (!config.Enabled || target == null)
            {
                return true;
            }

            bool split = config.SplitEyes && Mathf.Abs(screenRect.width - 0.5f) <= 0.01f && target.antiAliasing <= 1;
            // ClipToMirror needs its own texture per eye (SplitEyes).
            bool clip = config.ClipToMirror && split && !Plain;
            bool mask = config.FootprintMask && !Plain;
            if (!split && !clip && !mask)
            {
                return true;
            }

            var eye = new Eye(camera, target, screenRect, __0, __1, __2, __4, __5);
            if (MirrorDebug.Dumping && split)
            {
                // Same frame, same scene: the plain split render of this eye, to compare with the clipped/masked one.
                eye.Render(true, false, false, false, screenRect.x > 0.25f ? "ref_r" : "ref_l");
            }

            eye.Render(split, clip, mask, true, null);
            return false;
        }

        private readonly struct Eye
        {
            private readonly Camera _camera;
            private readonly RenderTexture _target;
            private readonly Rect _screenRect;
            private readonly Vector3 _position, _planePos, _planeNormal;
            private readonly Quaternion _rotation;
            private readonly Matrix4x4 _projection;

            internal Eye(Camera camera, RenderTexture target, Rect screenRect, Vector3 position, Quaternion rotation,
                Matrix4x4 projection, Vector3 planePos, Vector3 planeNormal)
            {
                _camera = camera;
                _target = target;
                _screenRect = screenRect;
                _position = position;
                _rotation = rotation;
                _projection = projection;
                _planePos = planePos;
                _planeNormal = planeNormal;
            }

            // copy: put the result into the game's texture (else only capture it as `capture`).
            internal void Render(bool split, bool clip, bool mask, bool copy, string? capture)
            {
                var camera = _camera;
                var target = _target;
                Rect screenRect = _screenRect;
                bool right = screenRect.x > 0.25f;
                MirrorMask.Clear(camera);

                // Pixel area of this eye's reflection to render: all of it, or (ClipToMirror) the bounding rectangle of the
                // mirrors' footprint; nothing if no mirror is in view.
                int width = split ? target.width / 2 : Mathf.RoundToInt(target.width * screenRect.width);
                int height = split ? target.height : Mathf.RoundToInt(target.height * screenRect.height);
                int px0 = 0, py0 = 0, px1 = width, py1 = height;
                bool footprint = false;
                if (clip || mask)
                {
                    var boxes = MirrorClip.MirrorBoxes();
                    if (boxes.Count > 0)
                    {
                        if (!MirrorClip.Footprint(boxes, _position, _rotation, _projection, Margin, Hull) || !MirrorClip.Bounds(Hull, out Rect ndc))
                        {
                            if (Time.frameCount % 600 == 0)
                            {
                                Plugin.Log.Info($"ClipToMirror eye x{screenRect.x:0.0}: no mirror in view, nothing rendered");
                            }

                            return;
                        }

                        footprint = true;
                        if (clip)
                        {
                            px0 = Mathf.Clamp(Mathf.FloorToInt((ndc.xMin + 1f) * 0.5f * width), 0, width);
                            px1 = Mathf.Clamp(Mathf.CeilToInt((ndc.xMax + 1f) * 0.5f * width), 0, width);
                            py0 = Mathf.Clamp(Mathf.FloorToInt((ndc.yMin + 1f) * 0.5f * height), 0, height);
                            py1 = Mathf.Clamp(Mathf.CeilToInt((ndc.yMax + 1f) * 0.5f * height), 0, height);
                            if (px1 <= px0 || py1 <= py0)
                            {
                                return;
                            }

                            // Round the size up to 128 px steps so the temporary texture pool sees few distinct sizes
                            // while the head moves.
                            Grow(ref px0, ref px1, width);
                            Grow(ref py0, ref py1, height);
                        }
                    }
                }

                bool partial = px0 > 0 || py0 > 0 || px1 < width || py1 < height;
                int rw = px1 - px0, rh = py1 - py0;
                int msaa = Mathf.Max(1, EyeAntiAliasing);
                // The area gets a texture of exactly its size. (A viewport inside a bigger texture makes a tiled GPU load
                // the rest of the multisampled surface instead of just clearing it, which can cost more than the pixels
                // saved.)
                var eye = split ? RenderTexture.GetTemporary(rw, rh, 24, target.format, RenderTextureReadWrite.Default, msaa) : target;
                camera.targetTexture = eye;
                camera.rect = split ? new Rect(0f, 0f, 1f, 1f) : screenRect;
                camera.projectionMatrix = _projection;
                Matrix4x4 reflection = ReflectionMatrix(Plane(_planePos, _planeNormal));
                camera.ResetWorldToCameraMatrix();
                camera.transform.SetPositionAndRotation(_position, _rotation);
                Matrix4x4 worldToCamera = camera.worldToCameraMatrix * reflection;
                camera.worldToCameraMatrix = worldToCamera;
                Matrix4x4 oblique = camera.CalculateObliqueMatrix(CameraSpacePlane(worldToCamera, _planePos, _planeNormal));
                camera.projectionMatrix = oblique;
                Rect? viewport = null;
                if (partial)
                {
                    // The projection stays the eye's own and the viewport keeps the full eye size, shifted so that the area
                    // lands on the texture (the GPU drops the rest). A projection for just the area would give the same
                    // geometry but move the screen position the game's shaders use to look up the bloom fog texture.
                    // Culling uses the area only.
                    viewport = new Rect(-px0, -py0, width, height);
                    Rect area = Rect.MinMaxRect(px0 * 2f / width - 1f, py0 * 2f / height - 1f, px1 * 2f / width - 1f, py1 * 2f / height - 1f);
                    camera.cullingMatrix = MirrorClip.SubProjection(oblique, area) * worldToCamera;
                }
                else
                {
                    camera.ResetCullingMatrix();
                }

                float unmasked = MirrorMask.Prepare(camera, viewport, mask && footprint ? Hull : null, right ? 1 : 0);
                if ((clip || mask) && copy && Time.frameCount % 600 == 0)
                {
                    Plugin.Log.Info($"ClipToMirror eye x{screenRect.x:0.0}: {rw}x{rh} of {width}x{height} px ({(float)rw * rh / (width * height):P1}), " +
                                    $"footprint {unmasked:P1}");
                }

                if (MirrorDebug.Dumping && copy && footprint)
                {
                    var exact = new List<Vector2>();
                    MirrorClip.Footprint(MirrorClip.MirrorBoxes(), _position, _rotation, _projection, 0f, exact);
                    MirrorDebug.LogFootprint(right ? "r" : "l", px0, py0, px1, py1, width, height, Hull, exact);
                }

                camera.Render();
                MirrorMask.Clear(camera);
                camera.ResetCullingMatrix();
                if (!split)
                {
                    return;
                }

                // Texture coordinates here start at the bottom left, like the viewport rectangle.
                int dstX = (right ? width : 0) + px0;
                string? name = capture ?? (MirrorDebug.Dumping ? (right ? "split_r" : "split_l") : null);
                if (msaa > 1)
                {
                    // ResolveAntiAliasedSurface(resolved) leaves `resolved` black here (Steam Frame, DXVK); sampling
                    // the multisampled texture in a blit resolves it correctly.
                    var resolved = RenderTexture.GetTemporary(rw, rh, 0, target.format, RenderTextureReadWrite.Default, 1);
                    Graphics.Blit(eye, resolved);
                    if (name != null)
                    {
                        MirrorDebug.Capture(resolved, name);
                    }

                    if (copy)
                    {
                        Graphics.CopyTexture(resolved, 0, 0, 0, 0, rw, rh, target, 0, 0, dstX, py0);
                    }

                    RenderTexture.ReleaseTemporary(resolved);
                }
                else
                {
                    if (name != null)
                    {
                        MirrorDebug.Capture(eye, name);
                    }

                    if (copy)
                    {
                        Graphics.CopyTexture(eye, 0, 0, 0, 0, rw, rh, target, 0, 0, dstX, py0);
                    }
                }

                camera.targetTexture = target;
                RenderTexture.ReleaseTemporary(eye);
            }
        }

        private static void Grow(ref int lo, ref int hi, int size)
        {
            int want = Mathf.Min(size, (hi - lo + 127) / 128 * 128);
            lo = Mathf.Clamp(lo - (want - (hi - lo)) / 2, 0, size - want);
            hi = lo + want;
        }

        // The standard planar mirror (as in Unity's MirrorReflection example): plane (n, d) with n.x + d = 0, reflection
        // R = I - 2 [n; 0] [n, d]^T, and the camera's near plane moved onto the mirror (oblique projection) so that
        // nothing behind it is drawn.
        private static Vector4 Plane(Vector3 pos, Vector3 normal) => new Vector4(normal.x, normal.y, normal.z, -Vector3.Dot(pos, normal));

        private static Vector4 CameraSpacePlane(Matrix4x4 worldToCamera, Vector3 pos, Vector3 normal) =>
            Plane(worldToCamera.MultiplyPoint(pos), worldToCamera.MultiplyVector(normal).normalized);

        private static Matrix4x4 ReflectionMatrix(Vector4 plane)
        {
            Matrix4x4 m = Matrix4x4.identity;
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    m[row, column] -= 2f * plane[row] * plane[column];
                }
            }

            return m;
        }
    }
}
