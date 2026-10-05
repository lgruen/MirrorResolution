using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MirrorResolution
{
    // The reflection texture is sampled at the mirror's own screen position, so only the part of each eye's view
    // that the mirror covers is ever seen. The game renders the whole reflected view at full resolution anyway
    // (looking ahead, the floor mirror is mostly or completely out of view). This finds, for one eye, a convex
    // polygon on screen that covers all active mirrors (their footprint); MirrorSplit renders just its bounding
    // rectangle (ClipToMirror) and MirrorMask keeps the scene out of the rest of that rectangle (FootprintMask).
    internal static class MirrorClip
    {
        private static readonly AccessTools.FieldRef<Mirror, MeshRenderer> MirrorRenderer =
            AccessTools.FieldRefAccess<Mirror, MeshRenderer>("_renderer");

        private static readonly List<(Mirror Mirror, MeshRenderer Renderer)> Mirrors = new List<(Mirror, MeshRenderer)>();
        private static readonly HashSet<Mirror> Known = new HashSet<Mirror>();
        private static readonly List<Bounds> Boxes = new List<Bounds>();
        private static readonly List<Vector2> Points = new List<Vector2>();
        private static readonly Vector4[] Clip = new Vector4[8];
        private static int _frame = -1;

        // Every enabled mirror runs Update each frame (before any camera renders) and OnWillRenderObject before its
        // reflection is rendered; both patches below register it here. (Looking the mirrors up with
        // FindObjectsByType instead walks all of the scene's ~15k MonoBehaviours: 5-10 ms on the main thread on the
        // Steam Frame, which made a frame late and reprojected every time it ran.)
        internal static void Register(Mirror mirror)
        {
            if (Known.Add(mirror))
            {
                Mirrors.Add((mirror, MirrorRenderer(mirror)));
                MirrorDebug.LogMirrors(Mirrors);
            }
        }

        // World bounds of every active mirror (all mirrors in a scene share one reflection texture).
        internal static List<Bounds> MirrorBoxes()
        {
            if (Time.frameCount != _frame)
            {
                _frame = Time.frameCount;
                Boxes.Clear();
                for (int i = Mirrors.Count - 1; i >= 0; i--)
                {
                    var (mirror, renderer) = Mirrors[i];
                    if (mirror == null)
                    {
                        // Destroyed (its scene was unloaded).
                        Known.Remove(mirror!);
                        Mirrors.RemoveAt(i);
                        continue;
                    }

                    if (mirror.isActiveAndEnabled && renderer != null && renderer.enabled)
                    {
                        Boxes.Add(renderer.bounds);
                    }
                }
            }

            return Boxes;
        }

        [HarmonyPatch(typeof(Mirror), "Update")]
        private static class UpdatePatch
        {
            private static void Prefix(Mirror __instance) => Register(__instance);
        }

        [HarmonyPatch(typeof(Mirror), "OnWillRenderObject")]
        private static class RenderPatch
        {
            private static void Prefix(Mirror __instance) => Register(__instance);
        }

        // Convex polygon (counter-clockwise, normalized device coordinates of the eye, may reach far outside -1..1)
        // that covers every box as seen by an eye at position/rotation with the given projection, grown by `margin`
        // on each side; false if no box is in front of the eye. Boxes are clipped against the eye plane, so corners
        // behind the eye (the mirror under the player's feet) are handled.
        internal static bool Footprint(List<Bounds> boxes, Vector3 position, Quaternion rotation, Matrix4x4 projection,
            float margin, List<Vector2> hull)
        {
            Matrix4x4 view = Matrix4x4.Scale(new Vector3(1f, 1f, -1f)) * Matrix4x4.TRS(position, rotation, Vector3.one).inverse;
            Matrix4x4 viewProjection = projection * view;
            const float minW = 1e-4f;
            Points.Clear();
            foreach (var box in boxes)
            {
                Vector3 min = box.min, max = box.max;
                for (int i = 0; i < 8; i++)
                {
                    var corner = new Vector3((i & 1) != 0 ? max.x : min.x, (i & 2) != 0 ? max.y : min.y, (i & 4) != 0 ? max.z : min.z);
                    Clip[i] = viewProjection * new Vector4(corner.x, corner.y, corner.z, 1f);
                }

                for (int i = 0; i < 8; i++)
                {
                    if (Clip[i].w > minW)
                    {
                        Add(Clip[i], margin);
                    }

                    // Where a box edge crosses the eye plane.
                    for (int bit = 1; bit < 8; bit <<= 1)
                    {
                        int j = i | bit;
                        if (j == i || (Clip[i].w > minW) == (Clip[j].w > minW))
                        {
                            continue;
                        }

                        float t = (Clip[i].w - minW) / (Clip[i].w - Clip[j].w);
                        Add(Vector4.Lerp(Clip[i], Clip[j], t), margin);
                    }
                }
            }

            ConvexHull(Points, hull);
            return hull.Count >= 3;
        }

        // A point grown into a square of half-size `margin` (the hull of all of them is the footprint grown by margin).
        private static void Add(Vector4 c, float margin)
        {
            float x = c.x / c.w, y = c.y / c.w;
            Points.Add(new Vector2(x - margin, y - margin));
            Points.Add(new Vector2(x + margin, y - margin));
            Points.Add(new Vector2(x + margin, y + margin));
            Points.Add(new Vector2(x - margin, y + margin));
        }

        // Andrew's monotone chain; counter-clockwise, no collinear points.
        private static void ConvexHull(List<Vector2> points, List<Vector2> hull)
        {
            hull.Clear();
            if (points.Count < 3)
            {
                return;
            }

            points.Sort((a, b) => a.x != b.x ? a.x.CompareTo(b.x) : a.y.CompareTo(b.y));
            for (int pass = 0; pass < 2; pass++)
            {
                int start = hull.Count;
                for (int k = 0; k < points.Count; k++)
                {
                    var p = points[pass == 0 ? k : points.Count - 1 - k];
                    while (hull.Count >= start + 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0f)
                    {
                        hull.RemoveAt(hull.Count - 1);
                    }

                    hull.Add(p);
                }

                hull.RemoveAt(hull.Count - 1);
            }
        }

        private static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.x - o.x) * (b.y - o.y) - (a.y - o.y) * (b.x - o.x);

        // Bounding rectangle of a polygon, limited to -1..1; false if empty.
        internal static bool Bounds(List<Vector2> polygon, out Rect ndc)
        {
            float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
            foreach (var p in polygon)
            {
                x0 = Mathf.Min(x0, p.x);
                x1 = Mathf.Max(x1, p.x);
                y0 = Mathf.Min(y0, p.y);
                y1 = Mathf.Max(y1, p.y);
            }

            x0 = Mathf.Max(x0, -1f);
            y0 = Mathf.Max(y0, -1f);
            x1 = Mathf.Min(x1, 1f);
            y1 = Mathf.Min(y1, 1f);
            ndc = Rect.MinMaxRect(x0, y0, x1, y1);
            return x0 < x1 && y0 < y1;
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
