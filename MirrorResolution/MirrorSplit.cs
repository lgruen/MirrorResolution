using System;
using HarmonyLib;
using UnityEngine;

namespace MirrorResolution
{
    // The game renders the stereo mirror as two camera renders into one side-by-side texture, each with a
    // half-width viewport. On the Frame's tiled GPU each of those passes still loads and stores the whole
    // double-wide surface and bins every tile. With SplitEyes, each eye renders into its own texture of
    // exactly its size, which is then resolved (MSAA) and copied into its half of the side-by-side texture.
    // With ClipToMirror (MirrorClip) only the part of each eye's view covered by the mirror is rendered.
    [HarmonyPatch(typeof(MirrorRendererSO), "RenderMirror")]
    internal static class MirrorSplit
    {
        // MSAA samples for the per-eye textures; the side-by-side texture is single-sampled when splitting.
        internal static int EyeAntiAliasing = 1;

        private static bool Prefix(Vector3 __0, Quaternion __1, Matrix4x4 __2, Rect __3, Vector3 __4, Vector3 __5, Camera ____mirrorCamera)
        {
            var config = PluginConfig.Instance;
            Rect screenRect = __3;
            RenderTexture? target = ____mirrorCamera.targetTexture;
            if (!config.Enabled || target == null)
            {
                return true;
            }

            bool split = config.SplitEyes && Mathf.Abs(screenRect.width - 0.5f) <= 0.01f && target.antiAliasing <= 1;
            bool clip = config.ClipToMirror;
            if (!split && !clip)
            {
                return true;
            }

            Vector3 camPosition = __0, planePos = __4, planeNormal = __5;
            Quaternion camRotation = __1;
            Matrix4x4 projection = __2;

            // Pixel area of this eye's reflection to render: all of it, or (ClipToMirror) the mirrors' screen
            // rectangle plus a margin for the mirror shader's normal-map offset; nothing if no mirror is in view.
            int width = split ? target.width / 2 : Mathf.RoundToInt(target.width * screenRect.width);
            int height = split ? target.height : Mathf.RoundToInt(target.height * screenRect.height);
            int px0 = 0, py0 = 0, px1 = width, py1 = height;
            if (clip && MirrorClip.MirrorBounds(out Bounds mirrors))
            {
                if (!MirrorClip.ScreenRect(mirrors, camPosition, camRotation, projection, out Rect ndc))
                {
                    if (Time.frameCount % 600 == 0)
                    {
                        Plugin.Log.Info($"ClipToMirror eye x{screenRect.x:0.0}: no mirror in view, nothing rendered");
                    }

                    return false;
                }

                const float margin = 0.05f;
                px0 = Mathf.Clamp(Mathf.FloorToInt((ndc.xMin - margin + 1f) * 0.5f * width), 0, width);
                px1 = Mathf.Clamp(Mathf.CeilToInt((ndc.xMax + margin + 1f) * 0.5f * width), 0, width);
                py0 = Mathf.Clamp(Mathf.FloorToInt((ndc.yMin - margin + 1f) * 0.5f * height), 0, height);
                py1 = Mathf.Clamp(Mathf.CeilToInt((ndc.yMax + margin + 1f) * 0.5f * height), 0, height);
                if (px1 <= px0 || py1 <= py0)
                {
                    return false;
                }
            }

            bool partial = px0 > 0 || py0 > 0 || px1 < width || py1 < height;
            if (clip && Time.frameCount % 600 == 0)
            {
                Plugin.Log.Info($"ClipToMirror eye x{screenRect.x:0.0}: {px1 - px0}x{py1 - py0} of {width}x{height} px ({(float)(px1 - px0) * (py1 - py0) / (width * height):P1})");
            }
            Rect area = Rect.MinMaxRect(px0 * 2f / width - 1f, py0 * 2f / height - 1f, px1 * 2f / width - 1f, py1 * 2f / height - 1f);
            int msaa = Mathf.Max(1, EyeAntiAliasing);
            var camera = ____mirrorCamera;
            var eye = split ? RenderTexture.GetTemporary(width, height, 24, target.format, RenderTextureReadWrite.Default, msaa) : target;
            Rect eyeRect = split ? new Rect(0f, 0f, 1f, 1f) : screenRect;
            camera.targetTexture = eye;
            camera.rect = new Rect(eyeRect.x + eyeRect.width * px0 / width, eyeRect.y + eyeRect.height * py0 / height,
                eyeRect.width * (px1 - px0) / width, eyeRect.height * (py1 - py0) / height);
            camera.projectionMatrix = projection;
            Matrix4x4 reflection = ReflectionMatrix(Plane(planePos, planeNormal));
            camera.ResetWorldToCameraMatrix();
            camera.transform.SetPositionAndRotation(camPosition, camRotation);
            Matrix4x4 worldToCamera = camera.worldToCameraMatrix * reflection;
            camera.worldToCameraMatrix = worldToCamera;
            Matrix4x4 oblique = camera.CalculateObliqueMatrix(CameraSpacePlane(worldToCamera, planePos, planeNormal));
            camera.projectionMatrix = partial ? MirrorClip.SubProjection(oblique, area) : oblique;
            camera.Render();
            if (!split)
            {
                return false;
            }

            int dstX = screenRect.x > 0.25f ? width : 0;
            if (msaa > 1)
            {
                var resolved = RenderTexture.GetTemporary(width, height, 0, target.format, RenderTextureReadWrite.Default, 1);
                eye.ResolveAntiAliasedSurface(resolved);
                Graphics.CopyTexture(resolved, 0, 0, 0, 0, width, height, target, 0, 0, dstX, 0);
                RenderTexture.ReleaseTemporary(resolved);
            }
            else
            {
                Graphics.CopyTexture(eye, 0, 0, 0, 0, width, height, target, 0, 0, dstX, 0);
            }

            camera.targetTexture = target;
            RenderTexture.ReleaseTemporary(eye);
            return false;
        }

        // Standard planar mirror math: reflection about the plane, oblique near plane.
        private static Vector4 Plane(Vector3 pos, Vector3 normal) => new Vector4(normal.x, normal.y, normal.z, -Vector3.Dot(pos, normal));

        private static Vector4 CameraSpacePlane(Matrix4x4 worldToCamera, Vector3 pos, Vector3 normal)
        {
            Vector3 p = worldToCamera.MultiplyPoint(pos);
            Vector3 n = worldToCamera.MultiplyVector(normal).normalized;
            return Plane(p, n);
        }

        private static Matrix4x4 ReflectionMatrix(Vector4 plane)
        {
            Matrix4x4 m = Matrix4x4.identity;
            m.m00 = 1f - 2f * plane[0] * plane[0];
            m.m01 = -2f * plane[0] * plane[1];
            m.m02 = -2f * plane[0] * plane[2];
            m.m03 = -2f * plane[3] * plane[0];
            m.m10 = -2f * plane[1] * plane[0];
            m.m11 = 1f - 2f * plane[1] * plane[1];
            m.m12 = -2f * plane[1] * plane[2];
            m.m13 = -2f * plane[3] * plane[1];
            m.m20 = -2f * plane[2] * plane[0];
            m.m21 = -2f * plane[2] * plane[1];
            m.m22 = 1f - 2f * plane[2] * plane[2];
            m.m23 = -2f * plane[3] * plane[2];
            return m;
        }
    }
}
