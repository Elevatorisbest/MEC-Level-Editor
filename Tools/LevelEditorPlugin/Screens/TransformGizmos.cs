using Frosty.Core.Viewport;
using FrostySdk.IO;
using LevelEditorPlugin.Diagnostics;
using LevelEditorPlugin.Render;
using SharpDX;
using System;
using System.Collections.Generic;
using System.IO;
using static Frosty.Core.Viewport.TextureUtils;

namespace LevelEditorPlugin.Screens;

public enum GizmoMode
{
    Move,
    Rotate,
    Scale,
}

/// <summary>
/// Meshes and textures for the rotate and scale gizmos, generated in code. They use Frosty's
/// GizmoShader: colour from "Texture", highlight from "SelectMask" (select index 0 = mask in
/// (0, 0.3), 1 = mask above 0.5, 2 = mask in (0.3, 0.5)). Axis index 0 = Z, 1 = Y, 2 = X, as for the
/// translate gizmo. The textures are 4x1 pixels: Z, Y, X, centre.
/// </summary>
internal static class GizmoAssets
{
    public const string ColorTexture = "Resources/Textures/LevelEditorGizmoAxes.dds";
    public const string MaskTexture = "Resources/Textures/LevelEditorGizmoAxesSelect.dds";

    private static readonly float[] AxisU = { 0.125f, 0.375f, 0.625f, 0.875f };
    private static bool texturesWritten;

    public static Vector3 Axis(int index) => index == 0 ? Vector3.UnitZ : index == 1 ? Vector3.UnitY : Vector3.UnitX;

    /// <summary>Writes the two small textures next to Frosty's own gizmo textures (once per session).</summary>
    public static void EnsureTextures()
    {
        if (texturesWritten)
            return;
        texturesWritten = true;
        try
        {
            // RGBA: Z blue, Y green, X red, centre light grey
            WriteDds(ColorTexture, new byte[]
            {
                 60, 110, 255, 255,
                 70, 220,  70, 255,
                235,  60,  60, 255,
                210, 210, 210, 255,
            });
            // select mask (red channel): Z 0.15 (index 0), Y 0.8 (index 1), X 0.4 (index 2), centre 0
            WriteDds(MaskTexture, new byte[]
            {
                 38, 0, 0, 255,
                204, 0, 0, 255,
                102, 0, 0, 255,
                  0, 0, 0, 255,
            });
        }
        catch (Exception ex)
        {
            LevelEditorLog.Warning("Gizmo", "Could not write gizmo textures", ex);
        }
    }

    private static void WriteDds(string path, byte[] rgba)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        DDSHeader header = new DDSHeader
        {
            dwHeight = 1,
            dwWidth = rgba.Length / 4,
            dwPitchOrLinearSize = rgba.Length,
            dwMipMapCount = 1,
            HasExtendedHeader = true,
        };
        header.ddspf.dwFourCC = 0x30315844; // "DX10"
        header.ExtendedHeader.dxgiFormat = SharpDX.DXGI.Format.R8G8B8A8_UNorm;
        header.ExtendedHeader.resourceDimension = SharpDX.Direct3D11.ResourceDimension.Texture2D;
        header.ExtendedHeader.arraySize = 1;

        using (NativeWriter writer = new NativeWriter(new FileStream(path, FileMode.Create, FileAccess.Write)))
        {
            header.Write(writer);
            writer.Write(rgba);
        }
    }

    private sealed class MeshBuilder
    {
        public readonly List<float> Vertices = new List<float>();
        public readonly List<uint> Indices = new List<uint>();
        private Vector3 min = new Vector3(float.MaxValue), max = new Vector3(float.MinValue);

        public uint Add(Vector3 p, Vector3 n, float u)
        {
            uint index = (uint)(Vertices.Count / 8);
            Vertices.AddRange(new[] { p.X, p.Y, p.Z, n.X, n.Y, n.Z, u, 0.5f });
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            return index;
        }

        public void Box(Vector3 bmin, Vector3 bmax, int column)
        {
            float u = AxisU[column];
            Vector3[] normals = { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
            foreach (Vector3 n in normals)
            {
                // two axes spanning the face
                Vector3 a = Math.Abs(n.X) > 0.5f ? Vector3.UnitY : Vector3.UnitX;
                Vector3 b = Vector3.Cross(n, a);
                Vector3 center = (bmin + bmax) * 0.5f;
                Vector3 half = (bmax - bmin) * 0.5f;
                Vector3 c = center + n * Vector3.Dot(Abs(n), half);
                Vector3 ea = a * Vector3.Dot(Abs(a), half);
                Vector3 eb = b * Vector3.Dot(Abs(b), half);
                uint i0 = Add(c - ea - eb, n, u);
                uint i1 = Add(c + ea - eb, n, u);
                uint i2 = Add(c + ea + eb, n, u);
                uint i3 = Add(c - ea + eb, n, u);
                Indices.AddRange(new[] { i0, i1, i2, i0, i2, i3 });
            }
        }

        public void Torus(int axisIndex, float radius, float tube, int segments, int sides)
        {
            float u = AxisU[axisIndex];
            Vector3 axis = Axis(axisIndex);
            Vector3 a = Math.Abs(axis.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
            Vector3 b = Vector3.Cross(axis, a);
            uint start = (uint)(Vertices.Count / 8);
            for (int i = 0; i <= segments; i++)
            {
                float t = i * MathUtil.TwoPi / segments;
                Vector3 dir = a * (float)Math.Cos(t) + b * (float)Math.Sin(t);
                for (int j = 0; j <= sides; j++)
                {
                    float p = j * MathUtil.TwoPi / sides;
                    Vector3 n = dir * (float)Math.Cos(p) + axis * (float)Math.Sin(p);
                    Add(dir * radius + n * tube, n, u);
                }
            }
            uint row = (uint)(sides + 1);
            for (uint i = 0; i < segments; i++)
            {
                for (uint j = 0; j < sides; j++)
                {
                    uint i0 = start + i * row + j;
                    uint i1 = start + (i + 1) * row + j;
                    Indices.AddRange(new[] { i0, i1, i1 + 1, i0, i1 + 1, i0 + 1 });
                }
            }
        }

        public ObjRenderable Build(RenderCreateState state)
        {
            return new ObjRenderable(state, Vertices.ToArray(), Indices.ToArray(), new BoundingBox(min, max));
        }

        private static Vector3 Abs(Vector3 v) => new Vector3(Math.Abs(v.X), Math.Abs(v.Y), Math.Abs(v.Z));
    }

    public const float RingRadius = 0.45f;
    public const float ScaleHandleLength = 0.42f;
    public const float ScaleCubeHalf = 0.035f;
    public const float ScaleCenterHalf = 0.05f;

    public static ObjRenderable CreateRotateMesh(RenderCreateState state)
    {
        MeshBuilder mb = new MeshBuilder();
        for (int axis = 0; axis < 3; axis++)
            mb.Torus(axis, RingRadius, 0.008f, 72, 8);
        return mb.Build(state);
    }

    public static ObjRenderable CreateScaleMesh(RenderCreateState state)
    {
        MeshBuilder mb = new MeshBuilder();
        const float bar = 0.008f;
        for (int axis = 0; axis < 3; axis++)
        {
            Vector3 d = Axis(axis);
            Vector3 thick = new Vector3(bar) - d * bar;
            mb.Box(-thick, d * ScaleHandleLength + thick, axis);
            Vector3 end = d * ScaleHandleLength;
            mb.Box(end - new Vector3(ScaleCubeHalf), end + new Vector3(ScaleCubeHalf), axis);
        }
        mb.Box(new Vector3(-ScaleCenterHalf), new Vector3(ScaleCenterHalf), 3);
        return mb.Build(state);
    }
}

/// <summary>
/// Rotation rings around the world X, Y and Z axes through the selection's origin. Dragging a ring
/// rotates by the angle the cursor sweeps around the centre, optionally snapped to SnapDegrees.
/// </summary>
public class RotateGizmo : Gizmo
{
    /// <summary>Angle step in degrees (0 = off).</summary>
    public static float SnapDegrees { get; set; }

    /// <summary>Rotation of the current drag (world space, about the gizmo centre).</summary>
    public Quaternion DeltaRotation { get; private set; } = Quaternion.Identity;
    public override bool IsDragging => moved;

    private int selectedAxis = -1;
    private bool leftMouseDown;
    private bool moved;
    private Vector3 center;
    private Vector3 axis;
    private Plane plane;
    private Vector3 lastDirection;
    private float totalAngle;

    public RotateGizmo()
    {
        proceduralMesh = true;
    }

    public override void CreateRenderProxy(RenderCreateState state)
    {
        GizmoAssets.EnsureTextures();
        meshData = GizmoAssets.CreateRotateMesh(state);
        renderProxy = new GizmoRenderProxy(state, meshData, GizmoAssets.ColorTexture, GizmoAssets.MaskTexture);
    }

    public override void Update(RenderCreateState state)
    {
        if (!IsVisible)
            return;
        BoundingBox = BoundingBox.FromSphere(new BoundingSphere(Transform.TranslationVector, WorldScale * 0.6f));
        renderProxy.UpdateSelectIndex(state, selectedAxis);
    }

    private int PickRing(Ray ray)
    {
        Vector3 c = Transform.TranslationVector;
        float radius = GizmoAssets.RingRadius * WorldScale;
        float tolerance = 0.06f * WorldScale;
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            Plane p = new Plane(c, GizmoAssets.Axis(i));
            if (ray.Intersects(ref p, out float distance))
            {
                Vector3 hit = ray.Position + ray.Direction * distance;
                if (Math.Abs((hit - c).Length() - radius) < tolerance && distance < bestDistance)
                {
                    best = i;
                    bestDistance = distance;
                }
            }
        }
        return best;
    }

    public override bool HitTest(Ray hitTestRay)
    {
        if (leftMouseDown)
            return true;
        if (!IsVisible)
            return false;

        selectedAxis = PickRing(hitTestRay);
        System.Windows.Input.Mouse.OverrideCursor = selectedAxis != -1 ? System.Windows.Input.Cursors.Hand : null;
        return selectedAxis != -1;
    }

    private bool DirectionOnPlane(Ray ray, out Vector3 direction)
    {
        direction = Vector3.Zero;
        if (!ray.Intersects(ref plane, out Vector3 hit))
            return false;
        Vector3 d = hit - center;
        if (d.LengthSquared() < 1e-10f)
            return false;
        direction = Vector3.Normalize(d);
        return true;
    }

    public override bool MouseDown(int x, int y, Ray rayCast, MouseButton button)
    {
        if (button != MouseButton.Left || selectedAxis == -1)
            return false;

        leftMouseDown = true;
        center = Transform.TranslationVector;
        axis = GizmoAssets.Axis(selectedAxis);
        plane = new Plane(center, axis);
        if (!DirectionOnPlane(rayCast, out lastDirection))
            lastDirection = Vector3.Zero;
        totalAngle = 0.0f;
        DeltaRotation = Quaternion.Identity;
        return true;
    }

    public override bool MouseMove(int x, int y, Ray rayCast)
    {
        if (!leftMouseDown)
            return false;

        moved = true;
        if (DirectionOnPlane(rayCast, out Vector3 direction))
        {
            if (lastDirection != Vector3.Zero)
            {
                // signed angle swept around the axis since the last move (accumulated, so more
                // than half a turn works)
                float sin = Vector3.Dot(Vector3.Cross(lastDirection, direction), axis);
                float cos = Vector3.Dot(lastDirection, direction);
                totalAngle += (float)Math.Atan2(sin, cos);
            }
            lastDirection = direction;
        }

        float angle = totalAngle;
        if (SnapDegrees > 0.0f)
        {
            float step = MathUtil.DegreesToRadians(SnapDegrees);
            angle = (float)Math.Round(angle / step) * step;
        }
        DeltaRotation = Quaternion.RotationAxis(axis, angle);
        return true;
    }

    public override bool MouseUp(int x, int y, Ray rayCast, MouseButton button)
    {
        if (!leftMouseDown)
            return false;

        bool wasMoved = moved;
        moved = false;
        leftMouseDown = false;
        selectedAxis = -1;
        System.Windows.Input.Mouse.OverrideCursor = null;
        return wasMoved;
    }
}

/// <summary>
/// Scale handles along the selection's own X, Y and Z axes, plus a centre box for uniform scale.
/// Axis handles scale by how far the cursor moves along the axis relative to the handle length;
/// the centre box by vertical mouse movement.
/// </summary>
public class ScaleGizmo : Gizmo
{
    /// <summary>Scale factor step (0 = off), e.g. 0.1 gives factors 0.9, 1.0, 1.1, ...</summary>
    public static float SnapStep { get; set; }

    /// <summary>Scale factors of the current drag along the object's local X, Y, Z.</summary>
    public Vector3 DeltaScale { get; private set; } = Vector3.One;
    public override bool IsDragging => moved;

    private const int Uniform = 3;
    private int selectedHandle = -1;
    private bool leftMouseDown;
    private bool moved;
    private Vector3 center;
    private Vector3 dragAxis;
    private Plane dragPlane;
    private float startAlong;
    private int startY;

    public ScaleGizmo()
    {
        proceduralMesh = true;
    }

    /// <summary>Object rotation; the handles follow the object's local axes.</summary>
    public Quaternion Orientation
    {
        get => renderProxy.Orientation;
        set => renderProxy.Orientation = value;
    }

    public override void CreateRenderProxy(RenderCreateState state)
    {
        GizmoAssets.EnsureTextures();
        meshData = GizmoAssets.CreateScaleMesh(state);
        renderProxy = new GizmoRenderProxy(state, meshData, GizmoAssets.ColorTexture, GizmoAssets.MaskTexture);
    }

    public override void Update(RenderCreateState state)
    {
        if (!IsVisible)
            return;
        BoundingBox = BoundingBox.FromSphere(new BoundingSphere(Transform.TranslationVector, WorldScale * 0.6f));
        renderProxy.UpdateSelectIndex(state, selectedHandle == Uniform ? -1 : selectedHandle);
    }

    private int PickHandle(Ray ray)
    {
        // test in the gizmo's local space (unit size, object-aligned)
        Matrix world = Matrix.Scaling(WorldScale) * Matrix.RotationQuaternion(Orientation) * Matrix.Translation(Transform.TranslationVector);
        Matrix inverse = Matrix.Invert(world);
        Ray local = new Ray(Vector3.TransformCoordinate(ray.Position, inverse), Vector3.Normalize(Vector3.TransformNormal(ray.Direction, inverse)));

        float centerHalf = GizmoAssets.ScaleCenterHalf * 1.4f;
        BoundingBox centerBox = new BoundingBox(new Vector3(-centerHalf), new Vector3(centerHalf));
        if (local.Intersects(ref centerBox))
            return Uniform;

        int best = -1;
        float bestDistance = float.MaxValue;
        float grab = GizmoAssets.ScaleCubeHalf * 1.6f;
        for (int i = 0; i < 3; i++)
        {
            Vector3 d = GizmoAssets.Axis(i);
            Vector3 side = new Vector3(grab) - d * grab;
            BoundingBox box = new BoundingBox(Vector3.Min(-side, d * (GizmoAssets.ScaleHandleLength + grab) + side), Vector3.Max(-side, d * (GizmoAssets.ScaleHandleLength + grab) + side));
            if (local.Intersects(ref box, out float distance) && distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }
        return best;
    }

    public override bool HitTest(Ray hitTestRay)
    {
        if (leftMouseDown)
            return true;
        if (!IsVisible)
            return false;

        selectedHandle = PickHandle(hitTestRay);
        System.Windows.Input.Mouse.OverrideCursor = selectedHandle != -1 ? System.Windows.Input.Cursors.SizeNWSE : null;
        return selectedHandle != -1;
    }

    private bool AlongAxis(Ray ray, out float along)
    {
        along = 0.0f;
        if (!ray.Intersects(ref dragPlane, out Vector3 hit))
            return false;
        along = Vector3.Dot(hit - center, dragAxis);
        return !float.IsNaN(along) && Math.Abs(along) < 100000.0f;
    }

    public override bool MouseDown(int x, int y, Ray rayCast, MouseButton button)
    {
        if (button != MouseButton.Left || selectedHandle == -1)
            return false;

        leftMouseDown = true;
        center = Transform.TranslationVector;
        startY = y;
        DeltaScale = Vector3.One;

        if (selectedHandle != Uniform)
        {
            dragAxis = Vector3.Transform(GizmoAssets.Axis(selectedHandle), Orientation);
            Vector3 normal = rayCast.Direction - dragAxis * Vector3.Dot(rayCast.Direction, dragAxis);
            if (normal.LengthSquared() < 1e-6f)
                normal = Vector3.Cross(dragAxis, Math.Abs(dragAxis.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX);
            normal.Normalize();
            dragPlane = new Plane(center, normal);
            if (!AlongAxis(rayCast, out startAlong))
                startAlong = GizmoAssets.ScaleHandleLength * WorldScale;
        }
        return true;
    }

    private static float SnapFactor(float factor)
    {
        if (SnapStep > 0.0f)
            factor = (float)Math.Round(factor / SnapStep) * SnapStep;
        return Math.Max(factor, 0.01f);
    }

    public override bool MouseMove(int x, int y, Ray rayCast)
    {
        if (!leftMouseDown)
            return false;

        moved = true;
        if (selectedHandle == Uniform)
        {
            // up = bigger; 150 px doubles/halves
            float factor = SnapFactor((float)Math.Pow(2.0, (startY - y) / 150.0));
            DeltaScale = new Vector3(factor);
        }
        else if (AlongAxis(rayCast, out float along))
        {
            float length = GizmoAssets.ScaleHandleLength * WorldScale;
            float factor = SnapFactor(1.0f + (along - startAlong) / length);
            Vector3 d = GizmoAssets.Axis(selectedHandle);
            DeltaScale = Vector3.One + d * (factor - 1.0f);
        }
        return true;
    }

    public override bool MouseUp(int x, int y, Ray rayCast, MouseButton button)
    {
        if (!leftMouseDown)
            return false;

        bool wasMoved = moved;
        moved = false;
        leftMouseDown = false;
        selectedHandle = -1;
        System.Windows.Input.Mouse.OverrideCursor = null;
        return wasMoved;
    }
}
