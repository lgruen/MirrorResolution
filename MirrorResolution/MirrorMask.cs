using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace MirrorResolution
{
    // FootprintMask: before the mirror camera draws the scene, the part of its viewport outside the mirrors' footprint
    // (MirrorClip.Footprint) gets the NEAR depth, so every scene fragment there fails the early depth test and is
    // never shaded. Those pixels are never sampled by the floor.
    //
    // Why the outside and not the footprint itself: the scene is drawn with one depth direction (Unity LEqual, which is
    // GEQUAL on the GPU with reversed Z). Marking the footprint instead (clear to near, write far inside) needs the
    // opposite direction for that draw, and Turnip turns LRZ off for the rest of the pass when a depth-writing draw
    // changes direction ("Depth write + compare-op direction change"); a depth clear inside the pass does too. Drawing
    // the outside at the near depth uses the scene's own direction, after the camera's normal clear.
    //
    // The outside of a convex polygon is drawn as quads without overlap: a strip below and above it, and between them
    // one quad per edge of its left and right chains out to the viewport edge.
    //
    // The same command buffers set ClipToMirror's shifted viewport (MirrorSplit).
    internal static class MirrorMask
    {
        // MIRROR_MASK=far (experiment, for comparing LRZ in a trace): clear depth to near inside the pass and write the
        // far depth inside the footprint instead. MIRROR_MASK_COLOR=1: paint the masked area magenta.
        private static readonly bool Far = Environment.GetEnvironmentVariable("MIRROR_MASK") == "far";
        private static readonly int ScreenParamsId = Shader.PropertyToID("_ScreenParams");
        // The game's blue-noise dither: its tiling (camera pixel size / noise texture size) is set as a global before
        // each camera renders, from the camera's pixel size, i.e. the clipped texture's.
        private static readonly int BlueNoiseParamsId = Shader.PropertyToID("_GlobalBlueNoiseParams");
        private static readonly int BlueNoiseTexId = Shader.PropertyToID("_GlobalBlueNoiseTex");
        private static readonly bool Show = Environment.GetEnvironmentVariable("MIRROR_MASK_COLOR") == "1";

        private static readonly List<Vector2> Polygon = new List<Vector2>();
        private static readonly List<Vector2> Scratch = new List<Vector2>();
        private static readonly List<Vector3> Vertices = new List<Vector3>();
        private static readonly List<int> Indices = new List<int>();
        private static readonly Mesh?[] Meshes = new Mesh?[2];
        private static CommandBuffer? _buffer;
        // Unity sets the viewport again before the skybox and the transparent queue; ClipToMirror's viewport is set
        // there too.
        private static readonly CameraEvent[] ViewportEvents = { CameraEvent.BeforeSkybox, CameraEvent.BeforeForwardAlpha };
        private static readonly CommandBuffer ViewportBuffer = new CommandBuffer { name = "MirrorResolution viewport" };
        private static Camera? _camera;
        private static Material? _material;
        private static bool _failed;

        // Fills the mirror camera's commands for its next Render(): an optional viewport (ClipToMirror), and the mask
        // for `footprint`, a convex polygon in the eye's normalized device coordinates (null: no mask). Returns the
        // share of the eye left unmasked (1 = no mask).
        internal static float Prepare(Camera camera, Rect? viewport, List<Vector2>? footprint, int eye)
        {
            var buffer = Attach(camera);
            buffer.Clear();
            ViewportBuffer.Clear();
            if (viewport.HasValue)
            {
                buffer.SetViewport(viewport.Value);
                ViewportBuffer.SetViewport(viewport.Value);
                // The eye's size, not the texture's, for shaders that work in screen pixels (text edges).
                float w = viewport.Value.width, h = viewport.Value.height;
                var screen = new Vector4(w, h, 1f + 1f / w, 1f + 1f / h);
                buffer.SetGlobalVector(ScreenParamsId, screen);
                ViewportBuffer.SetGlobalVector(ScreenParamsId, screen);
                // The dither tiled over the eye's size too; else it is stretched by eye size / texture size (vertical
                // streaks when only the platform's mirror is in view and the texture is 128 px high).
                var noise = Shader.GetGlobalTexture(BlueNoiseTexId);
                if (noise != null && noise.width > 0 && noise.height > 0)
                {
                    var tiling = new Vector4(w / noise.width, h / noise.height, 0f, 0f);
                    buffer.SetGlobalVector(BlueNoiseParamsId, tiling);
                    ViewportBuffer.SetGlobalVector(BlueNoiseParamsId, tiling);
                }
            }

            if (footprint == null || !CreateMaterial())
            {
                return 1f;
            }

            Polygon.Clear();
            Polygon.AddRange(footprint);
            ClipToViewport(Polygon);
            float area = Polygon.Count >= 3 ? Area(Polygon) / 4f : 0f;
            if (area > 0.98f)
            {
                return 1f;
            }

            var mesh = Meshes[eye] ??= CreateMesh();
            Vertices.Clear();
            Indices.Clear();
            if (Far)
            {
                for (int i = 1; i + 1 < Polygon.Count; i++)
                {
                    Triangle(Polygon[0], Polygon[i], Polygon[i + 1], 1f);
                }
            }
            else
            {
                Outside(Polygon, -1f);
            }

            mesh.Clear();
            mesh.SetVertices(Vertices);
            mesh.SetTriangles(Indices, 0, false);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(4f, 4f, 4f));

            // The mesh is in normalized device coordinates (OpenGL convention, like the camera's projection); Unity
            // converts the identity projection to the GPU's convention (flip, reversed Z) like any other: z = -1 is
            // the near plane (depth 1 on the GPU), z = 1 the far plane.
            if (Far)
            {
                buffer.ClearRenderTarget(true, false, Color.clear, 0f);
            }

            buffer.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            buffer.DrawMesh(mesh, Matrix4x4.identity, _material!, 0, 0);
            buffer.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
            return area;
        }

        // Empties the mask commands (mask off, or nothing to mask for this eye).
        internal static void Clear(Camera camera)
        {
            if (_buffer != null && _camera == camera)
            {
                _buffer.Clear();
                ViewportBuffer.Clear();
            }
        }

        private static CommandBuffer Attach(Camera camera)
        {
            if (_camera != camera || _buffer == null)
            {
                _buffer ??= new CommandBuffer { name = "MirrorResolution viewport and footprint mask" };
                if (_camera != null)
                {
                    _camera.RemoveCommandBuffer(CameraEvent.BeforeForwardOpaque, _buffer);
                    foreach (var e in ViewportEvents)
                    {
                        _camera.RemoveCommandBuffer(e, ViewportBuffer);
                    }
                }

                _camera = camera;
                camera.AddCommandBuffer(CameraEvent.BeforeForwardOpaque, _buffer);
                foreach (var e in ViewportEvents)
                {
                    camera.AddCommandBuffer(e, ViewportBuffer);
                }
            }

            return _buffer;
        }

        private static bool CreateMaterial()
        {
            if (_material != null)
            {
                return true;
            }

            if (_failed)
            {
                return false;
            }

            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null)
            {
                _failed = true;
                Plugin.Log.Warn("FootprintMask: shader Hidden/Internal-Colored not found, mask off");
                return false;
            }

            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _material.SetInt("_SrcBlend", (int)BlendMode.One);
            _material.SetInt("_DstBlend", (int)BlendMode.Zero);
            _material.SetInt("_Cull", (int)CullMode.Off);
            _material.SetInt("_ZWrite", 1);
            // Near outside the footprint over the cleared far: LEqual, the scene's own direction. (Far: far inside the
            // footprint over a near clear needs GEqual.)
            _material.SetInt("_ZTest", (int)(Far ? CompareFunction.GreaterEqual : CompareFunction.LessEqual));
            _material.SetColor("_Color", Show ? new Color(1f, 0f, 1f, 1f) : new Color(0f, 0f, 0f, 0f));
            Plugin.Log.Info($"FootprintMask: {(Far ? "far inside (experiment)" : "near outside")}{(Show ? ", shown in magenta" : "")}");
            return true;
        }

        private static Mesh CreateMesh()
        {
            var mesh = new Mesh { name = "MirrorResolution mask", hideFlags = HideFlags.HideAndDontSave };
            mesh.MarkDynamic();
            return mesh;
        }

        // The viewport square minus a convex counter-clockwise polygon that lies inside it.
        private static void Outside(List<Vector2> polygon, float z)
        {
            if (polygon.Count < 3)
            {
                Quad(new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(1f, 1f), new Vector2(-1f, 1f), z);
                return;
            }

            int bottom = 0, top = 0;
            for (int i = 1; i < polygon.Count; i++)
            {
                if (polygon[i].y < polygon[bottom].y)
                {
                    bottom = i;
                }

                if (polygon[i].y > polygon[top].y)
                {
                    top = i;
                }
            }

            float y0 = polygon[bottom].y, y1 = polygon[top].y;
            Quad(new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(1f, y0), new Vector2(-1f, y0), z);
            Quad(new Vector2(-1f, y1), new Vector2(1f, y1), new Vector2(1f, 1f), new Vector2(-1f, 1f), z);
            int n = polygon.Count;
            // Counter-clockwise from the bottom vertex runs up the right side, clockwise up the left.
            for (int i = bottom; i != top; i = (i + 1) % n)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % n];
                Quad(a, new Vector2(1f, a.y), new Vector2(1f, b.y), b, z);
            }

            for (int i = bottom; i != top; i = (i + n - 1) % n)
            {
                Vector2 a = polygon[i], b = polygon[(i + n - 1) % n];
                Quad(new Vector2(-1f, a.y), a, b, new Vector2(-1f, b.y), z);
            }
        }

        private static void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float z)
        {
            Triangle(a, b, c, z);
            Triangle(a, c, d, z);
        }

        private static void Triangle(Vector2 a, Vector2 b, Vector2 c, float z)
        {
            int n = Vertices.Count;
            Vertices.Add(new Vector3(a.x, a.y, z));
            Vertices.Add(new Vector3(b.x, b.y, z));
            Vertices.Add(new Vector3(c.x, c.y, z));
            Indices.Add(n);
            Indices.Add(n + 1);
            Indices.Add(n + 2);
        }

        private static float Area(List<Vector2> polygon)
        {
            float twice = 0f;
            for (int i = 0; i < polygon.Count; i++)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                twice += a.x * b.y - b.x * a.y;
            }

            return Mathf.Abs(twice) * 0.5f;
        }

        // Sutherland-Hodgman against the square -1..1 (keeps a convex polygon convex and its orientation).
        private static void ClipToViewport(List<Vector2> polygon)
        {
            for (int side = 0; side < 4; side++)
            {
                Scratch.Clear();
                Scratch.AddRange(polygon);
                polygon.Clear();
                for (int i = 0; i < Scratch.Count; i++)
                {
                    Vector2 a = Scratch[i], b = Scratch[(i + 1) % Scratch.Count];
                    float da = Inside(a, side), db = Inside(b, side);
                    if (da >= 0f)
                    {
                        polygon.Add(a);
                    }

                    if ((da >= 0f) != (db >= 0f))
                    {
                        polygon.Add(Vector2.Lerp(a, b, da / (da - db)));
                    }
                }
            }
        }

        // Signed distance inside one side of the square (>= 0: inside).
        private static float Inside(Vector2 p, int side) => side switch
        {
            0 => p.x + 1f,
            1 => 1f - p.x,
            2 => p.y + 1f,
            _ => 1f - p.y,
        };
    }
}
