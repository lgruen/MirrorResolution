using System;
using HarmonyLib;
using UnityEngine;

namespace MirrorResolution
{
    // The game renders the stereo mirror as two camera renders into one side-by-side texture, each with a
    // half-width viewport. On the Frame's tiled GPU each of those passes still loads and stores the whole
    // double-wide surface and bins every tile. With SplitEyes, each eye renders into its own texture of
    // exactly its size, which is then resolved (MSAA) and copied into its half of the side-by-side texture.
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
            if (!config.Enabled || !config.SplitEyes || target == null || Mathf.Abs(screenRect.width - 0.5f) > 0.01f || target.antiAliasing > 1)
            {
                return true;
            }

            Vector3 camPosition = __0, planePos = __4, planeNormal = __5;
            Quaternion camRotation = __1;
            Matrix4x4 projection = __2;
            int width = target.width / 2, height = target.height;
            int msaa = Mathf.Max(1, EyeAntiAliasing);

            var eye = RenderTexture.GetTemporary(width, height, 24, target.format, RenderTextureReadWrite.Default, msaa);
            var camera = ____mirrorCamera;
            camera.targetTexture = eye;
            camera.rect = new Rect(0f, 0f, 1f, 1f);
            camera.projectionMatrix = projection;
            Matrix4x4 reflection = ReflectionMatrix(Plane(planePos, planeNormal));
            camera.ResetWorldToCameraMatrix();
            camera.transform.SetPositionAndRotation(camPosition, camRotation);
            Matrix4x4 worldToCamera = camera.worldToCameraMatrix * reflection;
            camera.worldToCameraMatrix = worldToCamera;
            camera.projectionMatrix = camera.CalculateObliqueMatrix(CameraSpacePlane(worldToCamera, planePos, planeNormal));
            camera.Render();

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
