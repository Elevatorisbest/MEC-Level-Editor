using SharpDX;
using System;
using System.Collections.Generic;
using System.Linq;

namespace LevelEditorPlugin.Shapes;

public enum ShapeKind
{
    Box,
    Plane,
    Ramp,
    Stairs,
    Cylinder,
    Corner,
}

/// <summary>Dimensions of a shape in metres. Origin: bottom centre; X = width, Y = height, Z = depth.</summary>
public sealed class ShapeParameters
{
    public ShapeKind Kind { get; set; } = ShapeKind.Box;
    public float Width { get; set; } = 4.0f;
    public float Height { get; set; } = 1.0f;
    public float Depth { get; set; } = 4.0f;
    /// <summary>Stairs: number of steps.</summary>
    public int Steps { get; set; } = 8;
    /// <summary>Cylinder: number of sides. A cylinder is round: Width is its diameter, Depth is not used.</summary>
    public int Segments { get; set; } = 16;
    /// <summary>Corner: thickness of both arms.</summary>
    public float Thickness { get; set; } = 1.0f;
    /// <summary>Metres one texture repeat covers (world-scale UVs).</summary>
    public float TextureScale { get; set; } = 2.0f;

    public ShapeParameters Clone() => (ShapeParameters)MemberwiseClone();

    /// <summary>Short text for asset names and the log, e.g. "box_4x1x4".</summary>
    public string Describe()
    {
        string F(float v) => v.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture).Replace('.', 'p');
        string size = Kind == ShapeKind.Plane ? $"{F(Width)}x{F(Depth)}" : $"{F(Width)}x{F(Height)}x{F(Depth)}";
        switch (Kind)
        {
            case ShapeKind.Stairs: return $"stairs_{size}_{Steps}steps";
            case ShapeKind.Cylinder: return $"cylinder_d{F(Width)}x{F(Height)}_{Segments}sides";
            case ShapeKind.Corner: return $"corner_{size}_t{F(Thickness)}";
            default: return $"{Kind.ToString().ToLowerInvariant()}_{size}";
        }
    }
}

/// <summary>Triangle mesh with one vertex per face corner (flat shading).</summary>
public sealed class ShapeMesh
{
    public List<Vector3> Positions { get; } = new List<Vector3>();
    public List<Vector3> Normals { get; } = new List<Vector3>();
    public List<Vector3> Tangents { get; } = new List<Vector3>();
    public List<Vector3> Binormals { get; } = new List<Vector3>();
    public List<Vector2> Uvs { get; } = new List<Vector2>();
    /// <summary>Three indices per triangle; front faces wound so cross(v1-v0, v2-v0) points outwards.</summary>
    public List<int> Indices { get; } = new List<int>();

    public int TriangleCount => Indices.Count / 3;

    public BoundingBox Bounds
    {
        get
        {
            if (Positions.Count == 0)
                return new BoundingBox();
            Vector3 min = Positions[0], max = Positions[0];
            foreach (Vector3 p in Positions)
            {
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
            return new BoundingBox(min, max);
        }
    }
}

/// <summary>Oriented collision box in shape space (becomes a PamOBBCollisionEntity).</summary>
public struct CollisionBox
{
    /// <summary>Rotation and translation of the box centre (no scale).</summary>
    public Matrix Transform;
    public Vector3 HalfExtents;

    public CollisionBox(Vector3 center, Vector3 halfExtents, Matrix? rotation = null)
    {
        Transform = (rotation ?? Matrix.Identity) * Matrix.Translation(center);
        HalfExtents = halfExtents;
    }
}

/// <summary>
/// Builds the render mesh and the collision boxes of a shape. Pure geometry (no Frosty), so it
/// can be tested without the editor.
///
/// Every shape is an extrusion of a 2D outline: box, ramp, stairs and plane are side profiles
/// (in Z/Y) pushed along X; cylinder and corner are floor outlines (in X/Z) pushed up along Y.
/// Faces have their own vertices (flat normals). UVs are world-scale box mapping so a tiling
/// material repeats every <see cref="ShapeParameters.TextureScale"/> metres whatever the size.
/// </summary>
public static class ShapeGenerator
{
    public const float MinSize = 0.05f;
    public const float MaxSize = 512.0f;
    /// <summary>Thickness of collision slabs under planes and ramp surfaces.</summary>
    public const float SlabThickness = 0.3f;

    /// <summary>Null when the parameters are usable, otherwise what is wrong.</summary>
    public static string Validate(ShapeParameters p)
    {
        bool Bad(float v) => float.IsNaN(v) || v < MinSize || v > MaxSize;
        if (Bad(p.Width) || (p.Kind != ShapeKind.Cylinder && Bad(p.Depth)) || (p.Kind != ShapeKind.Plane && Bad(p.Height)))
            return $"Sizes must be between {MinSize} and {MaxSize} m.";
        if (p.Kind == ShapeKind.Stairs && (p.Steps < 1 || p.Steps > 64))
            return "Stairs need 1 to 64 steps.";
        if (p.Kind == ShapeKind.Cylinder && (p.Segments < 3 || p.Segments > 64))
            return "A cylinder needs 3 to 64 sides.";
        if (p.Kind == ShapeKind.Corner && (p.Thickness < MinSize || p.Thickness >= Math.Min(p.Width, p.Depth)))
            return "Corner thickness must be smaller than both width and depth.";
        if (float.IsNaN(p.TextureScale) || p.TextureScale < 0.05f || p.TextureScale > 1000.0f)
            return "Texture size must be between 0.05 and 1000 m.";
        return null;
    }

    public static ShapeMesh BuildMesh(ShapeParameters p)
    {
        ShapeMesh mesh = new ShapeMesh();
        float w = p.Width, h = p.Height, d = p.Depth;
        switch (p.Kind)
        {
            case ShapeKind.Box:
                ExtrudeAlongX(mesh, new[] { V(-d / 2, 0), V(d / 2, 0), V(d / 2, h), V(-d / 2, h) }, w, p.TextureScale);
                break;
            case ShapeKind.Ramp:
                // rises from the front (-Z, height 0) to the back (+Z, height h)
                ExtrudeAlongX(mesh, new[] { V(-d / 2, 0), V(d / 2, 0), V(d / 2, h) }, w, p.TextureScale);
                break;
            case ShapeKind.Stairs:
                ExtrudeAlongX(mesh, StairProfile(p), w, p.TextureScale);
                break;
            case ShapeKind.Plane:
                AddQuad(mesh, new Vector3(-w / 2, 0, -d / 2), new Vector3(-w / 2, 0, d / 2), new Vector3(w / 2, 0, d / 2), new Vector3(w / 2, 0, -d / 2), Vector3.UnitY, p.TextureScale);
                break;
            case ShapeKind.Cylinder:
                {
                    Vector2[] outline = new Vector2[p.Segments];
                    for (int i = 0; i < p.Segments; i++)
                    {
                        double a = 2.0 * Math.PI * i / p.Segments;
                        outline[i] = V((float)(Math.Cos(a) * w / 2), (float)(Math.Sin(a) * w / 2));
                    }
                    ExtrudeUp(mesh, outline, h, p.TextureScale);
                    break;
                }
            case ShapeKind.Corner:
                ExtrudeUp(mesh, CornerOutline(p), h, p.TextureScale);
                break;
        }
        return mesh;
    }

    public static List<CollisionBox> BuildCollision(ShapeParameters p)
    {
        List<CollisionBox> boxes = new List<CollisionBox>();
        float w = p.Width, h = p.Height, d = p.Depth;
        switch (p.Kind)
        {
            case ShapeKind.Box:
                boxes.Add(new CollisionBox(new Vector3(0, h / 2, 0), new Vector3(w / 2, h / 2, d / 2)));
                break;
            case ShapeKind.Plane:
                // a slab just under the surface, so the plane can be walked on from above
                boxes.Add(new CollisionBox(new Vector3(0, -SlabThickness / 2, 0), new Vector3(w / 2, SlabThickness / 2, d / 2)));
                break;
            case ShapeKind.Ramp:
                {
                    // slab under the sloped surface, tilted with it, plus a slab at the high back
                    float length = (float)Math.Sqrt(d * d + h * h);
                    float angle = (float)Math.Atan2(h, d);
                    float t = Math.Min(SlabThickness, Math.Min(h, d) * 0.5f);
                    Vector3 normal = new Vector3(0, d, -h) / length;
                    Vector3 center = new Vector3(0, h / 2, 0) - normal * (t / 2);
                    boxes.Add(new CollisionBox(center, new Vector3(w / 2, t / 2, length / 2), Matrix.RotationX(-angle)));
                    float back = Math.Min(SlabThickness, d * 0.5f);
                    boxes.Add(new CollisionBox(new Vector3(0, h / 2, d / 2 - back / 2), new Vector3(w / 2, h / 2, back / 2)));
                    break;
                }
            case ShapeKind.Stairs:
                {
                    float stepDepth = d / p.Steps;
                    for (int i = 0; i < p.Steps; i++)
                    {
                        float top = h * (i + 1) / p.Steps;
                        float z = -d / 2 + stepDepth * (i + 0.5f);
                        boxes.Add(new CollisionBox(new Vector3(0, top / 2, z), new Vector3(w / 2, top / 2, stepDepth / 2)));
                    }
                    break;
                }
            case ShapeKind.Cylinder:
                {
                    // Round (width = diameter). One box per pair of opposite sides (per side for an
                    // odd count), each spanning the polygon across that side: together they cover it.
                    int n = p.Segments;
                    float r = w / 2;
                    float apothem = r * (float)Math.Cos(Math.PI / n);
                    float side = 2 * r * (float)Math.Sin(Math.PI / n);
                    float far = n % 2 == 0 ? apothem : r;
                    int count = n % 2 == 0 ? n / 2 : n;
                    for (int k = 0; k < count; k++)
                    {
                        double theta = 2 * Math.PI * k / n + Math.PI / n; // direction of side k's midpoint
                        float offset = (apothem - far) / 2;
                        Vector3 center = new Vector3((float)Math.Cos(theta) * offset, h / 2, (float)Math.Sin(theta) * offset);
                        boxes.Add(new CollisionBox(center, new Vector3((apothem + far) / 2, h / 2, side / 2), Matrix.RotationY(-(float)theta)));
                    }
                    break;
                }
            case ShapeKind.Corner:
                {
                    float t = p.Thickness;
                    // arm along X at the back, arm along Z on the left
                    boxes.Add(new CollisionBox(new Vector3(0, h / 2, d / 2 - t / 2), new Vector3(w / 2, h / 2, t / 2)));
                    boxes.Add(new CollisionBox(new Vector3(-w / 2 + t / 2, h / 2, -t / 2), new Vector3(t / 2, h / 2, (d - t) / 2)));
                    break;
                }
        }
        return boxes;
    }

    private static Vector2 V(float a, float b) => new Vector2(a, b);

    /// <summary>Side profile of stairs in (Z, Y): rising towards +Z.</summary>
    private static Vector2[] StairProfile(ShapeParameters p)
    {
        List<Vector2> points = new List<Vector2> { V(-p.Depth / 2, 0), V(p.Depth / 2, 0) };
        float stepDepth = p.Depth / p.Steps, stepHeight = p.Height / p.Steps;
        // back wall top, then down the steps towards the front
        for (int i = p.Steps; i >= 1; i--)
        {
            float z = -p.Depth / 2 + stepDepth * i;
            float y = stepHeight * i;
            if (i == p.Steps)
                points.Add(V(z, y));
            points.Add(V(z - stepDepth, y));
            if (i > 1)
                points.Add(V(z - stepDepth, y - stepHeight));
        }
        return points.ToArray();
    }

    /// <summary>Floor outline of an L: arm along the back (+Z) and arm along the left (-X).</summary>
    private static Vector2[] CornerOutline(ShapeParameters p)
    {
        float w = p.Width, d = p.Depth, t = p.Thickness;
        return new[]
        {
            V(-w / 2, -d / 2), V(-w / 2 + t, -d / 2), V(-w / 2 + t, d / 2 - t),
            V(w / 2, d / 2 - t), V(w / 2, d / 2), V(-w / 2, d / 2),
        };
    }

    /// <summary>Extrudes a side profile in (Z, Y) along X from -width/2 to +width/2.</summary>
    private static void ExtrudeAlongX(ShapeMesh mesh, Vector2[] profile, float width, float tile)
    {
        Vector3 To3(Vector2 q, float x) => new Vector3(x, q.Y, q.X);
        ExtrudeGeneric(mesh, profile, To3, -width / 2, width / 2, Vector3.UnitX, tile);
    }

    /// <summary>Extrudes a floor outline in (X, Z) along Y from 0 to height.</summary>
    private static void ExtrudeUp(ShapeMesh mesh, Vector2[] outline, float height, float tile)
    {
        Vector3 To3(Vector2 q, float y) => new Vector3(q.X, y, q.Y);
        ExtrudeGeneric(mesh, outline, To3, 0, height, Vector3.UnitY, tile);
    }

    private static void ExtrudeGeneric(ShapeMesh mesh, Vector2[] outline, Func<Vector2, float, Vector3> to3, float from, float to, Vector3 axis, float tile)
    {
        // caps: the outline triangulated, one cap facing -axis, one +axis
        List<int[]> triangles = Triangulate(outline);
        AddCap(mesh, outline, triangles, q => to3(q, to), axis, tile);
        AddCap(mesh, outline, triangles, q => to3(q, from), -axis, tile);

        // sides: one quad per outline edge, facing away from the outline's inside
        Vector3 centroid = Vector3.Zero;
        foreach (Vector2 q in outline)
            centroid += to3(q, (from + to) / 2);
        centroid /= outline.Length;
        bool ccw = SignedArea(outline) > 0;
        for (int i = 0; i < outline.Length; i++)
        {
            Vector2 a = outline[i], b = outline[(i + 1) % outline.Length];
            Vector3 a0 = to3(a, from), b0 = to3(b, from), a1 = to3(a, to), b1 = to3(b, to);
            Vector3 edge = b0 - a0;
            if (edge.LengthSquared() < 1e-10f)
                continue;
            // outward normal of the edge: perpendicular to edge and axis, away from the inside
            Vector3 n = Vector3.Normalize(Vector3.Cross(edge, axis));
            Vector2 e2 = b - a;
            Vector2 inside2 = ccw ? new Vector2(-e2.Y, e2.X) : new Vector2(e2.Y, -e2.X); // left of edge for ccw outlines
            Vector3 inside3 = to3(a + inside2, (from + to) / 2) - to3(a, (from + to) / 2);
            if (Vector3.Dot(n, inside3) > 0)
                n = -n;
            AddQuad(mesh, a0, b0, b1, a1, n, tile);
        }
    }

    private static void AddCap(ShapeMesh mesh, Vector2[] outline, List<int[]> triangles, Func<Vector2, Vector3> place, Vector3 normal, float tile)
    {
        foreach (int[] tri in triangles)
            AddTriangle(mesh, place(outline[tri[0]]), place(outline[tri[1]]), place(outline[tri[2]]), normal, tile);
    }

    /// <summary>Adds a quad (two triangles) with its corners in order around the face.</summary>
    private static void AddQuad(ShapeMesh mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal, float tile)
    {
        AddTriangle(mesh, a, b, c, normal, tile);
        AddTriangle(mesh, a, c, d, normal, tile);
    }

    /// <summary>Adds one flat triangle, wound so it faces <paramref name="normal"/>, with world-scale UVs.</summary>
    private static void AddTriangle(ShapeMesh mesh, Vector3 a, Vector3 b, Vector3 c, Vector3 normal, float tile)
    {
        if (Vector3.Dot(Vector3.Cross(b - a, c - a), normal) < 0)
        {
            Vector3 tmp = b;
            b = c;
            c = tmp;
        }

        // box mapping: project onto the plane of the two axes the normal is least aligned with;
        // vertical faces keep "up" as -V so textures stand upright
        Vector3 an = new Vector3(Math.Abs(normal.X), Math.Abs(normal.Y), Math.Abs(normal.Z));
        Vector3 uAxis, vAxis;
        if (an.Y >= an.X && an.Y >= an.Z)
        {
            uAxis = Vector3.UnitX;
            vAxis = Vector3.UnitZ;
        }
        else if (an.X >= an.Z)
        {
            uAxis = Vector3.UnitZ * Math.Sign(normal.X == 0 ? 1 : normal.X);
            vAxis = -Vector3.UnitY;
        }
        else
        {
            uAxis = -Vector3.UnitX * Math.Sign(normal.Z == 0 ? 1 : normal.Z);
            vAxis = -Vector3.UnitY;
        }

        // tangent space follows the UV directions, made perpendicular to the normal
        Vector3 tangent = Vector3.Normalize(uAxis - normal * Vector3.Dot(uAxis, normal));
        Vector3 binormal = Vector3.Normalize(Vector3.Cross(normal, tangent));
        if (Vector3.Dot(binormal, vAxis) < 0)
            binormal = -binormal;

        int start = mesh.Positions.Count;
        foreach (Vector3 p in new[] { a, b, c })
        {
            mesh.Positions.Add(p);
            mesh.Normals.Add(normal);
            mesh.Tangents.Add(tangent);
            mesh.Binormals.Add(binormal);
            mesh.Uvs.Add(new Vector2(Vector3.Dot(p, uAxis) / tile, Vector3.Dot(p, vAxis) / tile));
        }
        mesh.Indices.Add(start);
        mesh.Indices.Add(start + 1);
        mesh.Indices.Add(start + 2);
    }

    private static float SignedArea(Vector2[] polygon)
    {
        float area = 0;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Length];
            area += a.X * b.Y - b.X * a.Y;
        }
        return area / 2;
    }

    /// <summary>Ear clipping for a simple polygon (no holes). Returns index triples.</summary>
    public static List<int[]> Triangulate(Vector2[] polygon)
    {
        List<int[]> result = new List<int[]>();
        List<int> remaining = Enumerable.Range(0, polygon.Length).ToList();
        bool ccw = SignedArea(polygon) > 0;
        int guard = 0;
        while (remaining.Count > 3 && guard++ < 10000)
        {
            bool clipped = false;
            for (int i = 0; i < remaining.Count; i++)
            {
                int ia = remaining[(i + remaining.Count - 1) % remaining.Count], ib = remaining[i], ic = remaining[(i + 1) % remaining.Count];
                Vector2 a = polygon[ia], b = polygon[ib], c = polygon[ic];
                float cross = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (ccw ? cross <= 1e-9f : cross >= -1e-9f)
                    continue; // reflex or flat corner
                bool contains = false;
                foreach (int j in remaining)
                {
                    if (j == ia || j == ib || j == ic)
                        continue;
                    if (PointInTriangle(polygon[j], a, b, c))
                    {
                        contains = true;
                        break;
                    }
                }
                if (contains)
                    continue;
                result.Add(new[] { ia, ib, ic });
                remaining.RemoveAt(i);
                clipped = true;
                break;
            }
            if (!clipped)
                break; // degenerate outline; fall back below
        }
        if (remaining.Count >= 3)
        {
            // remaining polygon is a triangle, or a degenerate case: fan it
            for (int i = 1; i + 1 < remaining.Count; i++)
                result.Add(new[] { remaining[0], remaining[i], remaining[i + 1] });
        }
        return result;
    }

    private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        float d1 = (p.X - b.X) * (a.Y - b.Y) - (a.X - b.X) * (p.Y - b.Y);
        float d2 = (p.X - c.X) * (b.Y - c.Y) - (b.X - c.X) * (p.Y - c.Y);
        float d3 = (p.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (p.Y - a.Y);
        bool neg = d1 < 0 || d2 < 0 || d3 < 0;
        bool pos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(neg && pos);
    }
}
