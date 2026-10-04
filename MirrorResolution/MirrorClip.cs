using HarmonyLib;
using UnityEngine;

namespace MirrorResolution
{
    // The reflection texture is sampled at the mirror's own screen position, so only the part of each eye's view
    // that the mirror covers is ever seen. The game renders the whole reflected view at full resolution anyway
    // (looking ahead, the floor mirror is mostly or completely out of view). This finds the screen rectangle of all
    // active mirrors for one eye; MirrorRender then renders just that rectangle with a matching off-centre
    // projection, which gives the same pixels there (and lets Unity cull everything outside it).
    internal static class MirrorClip
    {
        private static readonly AccessTools.FieldRef<Mirror, MeshRenderer> MirrorRenderer =
            AccessTools.FieldRefAccess<Mirror, MeshRenderer>("_renderer");

        private static int _frame = -1;
        private static Bounds _bounds;
        private static bool _any;

        // Union of the bounds of every active mirror (all mirrors in a scene share one reflection texture).
        internal static bool MirrorBounds(out Bounds bounds)
        {
            if (Time.frameCount != _frame)
            {
                _frame = Time.frameCount;
                _any = false;
                foreach (var mirror in Object.FindObjectsByType<Mirror>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
                {
                    var renderer = MirrorRenderer(mirror);
                    if (!mirror.isActiveAndEnabled || renderer == null || !renderer.enabled)
                    {
                        continue;
                    }

                    if (_any)
                    {
                        _bounds.Encapsulate(renderer.bounds);
                    }
                    else
                    {
                        _bounds = renderer.bounds;
                        _any = true;
                    }
                }
            }

            bounds = _bounds;
            return _any;
        }

        // Rectangle in normalized device coordinates (-1..1) covered by the box for an eye at position/rotation
        // with the given projection; false if none of it is in view. The box is clipped against the eye plane, so
        // corners behind the eye (the mirror under the player's feet) are handled.
        internal static bool ScreenRect(Bounds box, Vector3 position, Quaternion rotation, Matrix4x4 projection, out Rect ndc)
        {
            ndc = default;
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(position, rotation, Vector3.one).inverse;
            Matrix4x4 viewProjection = projection * view;
            var clip = new Vector4[8];
            Vector3 min = box.min, max = box.max;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) != 0 ? max.x : min.x, (i & 2) != 0 ? max.y : min.y, (i & 4) != 0 ? max.z : min.z);
                clip[i] = viewProjection * new Vector4(corner.x, corner.y, corner.z, 1f);
            }

            const float minW = 1e-4f;
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            bool any = false;

            void Add(Vector4 c)
            {
                float x = c.x / c.w, y = c.y / c.w;
                x0 = Mathf.Min(x0, x);
                x1 = Mathf.Max(x1, x);
                y0 = Mathf.Min(y0, y);
                y1 = Mathf.Max(y1, y);
                any = true;
            }

            for (int i = 0; i < 8; i++)
            {
                if (clip[i].w > minW)
                {
                    Add(clip[i]);
                }

                for (int bit = 1; bit < 8; bit <<= 1)
                {
                    int j = i | bit;
                    if (j == i || (clip[i].w > minW) == (clip[j].w > minW))
                    {
                        continue;
                    }

                    float t = (clip[i].w - minW) / (clip[i].w - clip[j].w);
                    Add(Vector4.Lerp(clip[i], clip[j], t));
                }
            }

            x0 = Mathf.Max(x0, -1f);
            y0 = Mathf.Max(y0, -1f);
            x1 = Mathf.Min(x1, 1f);
            y1 = Mathf.Min(y1, 1f);
            if (!any || x0 >= x1 || y0 >= y1)
            {
                return false;
            }

            ndc = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }

        // Projection that maps the NDC rectangle onto the whole viewport: the camera then renders exactly the
        // pixels that the full projection puts inside the rectangle (only rows x and y change, so depth and the
        // oblique near plane are untouched).
        internal static Matrix4x4 SubProjection(Matrix4x4 projection, Rect ndc)
        {
            Matrix4x4 s = Matrix4x4.identity;
            s.m00 = 2f / ndc.width;
            s.m03 = -(ndc.xMin + ndc.xMax) / ndc.width;
            s.m11 = 2f / ndc.height;
            s.m13 = -(ndc.yMin + ndc.yMax) / ndc.height;
            return s * projection;
        }
    }
}
