using Frosty.Core;
using Frosty.Core.Viewport;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers;
using FrostySdk.Managers.Entries;
using LevelEditorPlugin.Options;
using MeshSetPlugin.Render;
using MeshSetPlugin.Resources;
using SharpDX;
using SharpDX.Direct3D;
using System.Collections.Generic;
using System.IO;
using D3D11 = SharpDX.Direct3D11;
using MeshSet = LevelEditorPlugin.Resources.MeshSet;
using MeshSetLod = LevelEditorPlugin.Resources.MeshSetLod;
using MeshSetSection = LevelEditorPlugin.Resources.MeshSetSection;
using LevelEditorPlugin.Diagnostics;

namespace LevelEditorPlugin.Render;

public struct MeshVertex
{
    public Vector3 Position;
    public Vector3 Normal;
}

public class MeshSectionRenderable
{
    public int StartIndex { get; private set; }
    public int VertexOffset { get; private set; }
    public int PrimitiveCount { get; private set; }
    public PrimitiveTopology PrimitiveType { get; private set; }
    public int VertexStride { get; private set; }

    public D3D11.Buffer VertexBuffer { get; private set; }
    public D3D11.VertexBufferBinding VertexBufferBinding { get; private set; }

    public List<Vector3> HitProxyVertices { get; private set; } = [];

    public MeshSectionRenderable(RenderCreateState state, MeshSetSection section, byte[] chunkData, bool isHitProxy)
    {
        StartIndex = (int)section.StartIndex;
        VertexOffset = (int)section.VertexOffset;
        PrimitiveCount = (int)section.PrimitiveCount;
        VertexStride = (int)Utilities.SizeOf<MeshVertex>();

        switch (section.PrimitiveType)
        {
            case Resources.PrimitiveType.PrimitiveType_LineList: PrimitiveType = PrimitiveTopology.LineList; break;
            case Resources.PrimitiveType.PrimitiveType_LineStrip: PrimitiveType = PrimitiveTopology.LineStrip; break;
            case Resources.PrimitiveType.PrimitiveType_PointList: PrimitiveType = PrimitiveTopology.PointList; break;
            case Resources.PrimitiveType.PrimitiveType_TriangleList: PrimitiveType = PrimitiveTopology.TriangleList; break;
            case Resources.PrimitiveType.PrimitiveType_TriangleStrip: PrimitiveType = PrimitiveTopology.TriangleStrip; break;
        }

        MeshVertex[] vertices = ReadVertices(section, chunkData);
        if (isHitProxy)
        {
            foreach (MeshVertex vertex in vertices)
                HitProxyVertices.Add(vertex.Position);
        }

        int size = (int)(Utilities.SizeOf<MeshVertex>() * section.VertexCount);
        using (DataStream stream = new DataStream(size, false, true))
        {
            stream.WriteRange<MeshVertex>(vertices);
            stream.Position = 0;

            VertexBuffer = new D3D11.Buffer(state.Device, stream, size, D3D11.ResourceUsage.Default, D3D11.BindFlags.VertexBuffer, D3D11.CpuAccessFlags.None, D3D11.ResourceOptionFlags.None, 0);
            VertexBufferBinding = new D3D11.VertexBufferBinding(VertexBuffer, VertexStride, 0);
        }
    }

    /// <summary>Reads position and normal for every vertex of a section from the mesh chunk.</summary>
    internal static MeshVertex[] ReadVertices(MeshSetSection section, byte[] chunkData)
    {
        MeshVertex[] vertices = new MeshVertex[section.VertexCount];
        using (NativeReader reader = new NativeReader(new MemoryStream(chunkData)))
        {
            reader.Position = section.VertexOffset;
            int totalStride = 0;

            foreach (GeometryDeclarationDesc.Stream stream in section.GeometryDeclDesc[0].Streams)
            {
                if (stream.VertexStride == 0)
                    continue;

                for (int v = 0; v < section.VertexCount; v++)
                {
                    MeshVertex vertex = vertices[v];
                    int currentStride = 0;

                    foreach (GeometryDeclarationDesc.Element element in section.GeometryDeclDesc[0].Elements)
                    {
                        if (element.Usage == VertexElementUsage.Unknown)
                            continue;

                        if (currentStride >= totalStride && currentStride < (totalStride + stream.VertexStride))
                        {
                            if (element.Usage == VertexElementUsage.Pos)
                            {
                                switch (element.Format)
                                {
                                    case VertexElementFormat.Float3: vertex.Position = new Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()); break;
                                    case VertexElementFormat.Float4: vertex.Position = new Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()); reader.ReadFloat(); break;
                                    case VertexElementFormat.Half3: vertex.Position = new Vector3(HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort())); break;
                                    case VertexElementFormat.Half4: vertex.Position = new Vector3(HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort())); reader.ReadUShort(); break;
                                }
                            }
                            else if (element.Usage == VertexElementUsage.Normal)
                            {
                                switch (element.Format)
                                {
                                    case VertexElementFormat.Float3: vertex.Normal = new Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()); break;
                                    case VertexElementFormat.Float4: vertex.Normal = new Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()); reader.ReadFloat(); break;
                                    case VertexElementFormat.Half3: vertex.Normal = new Vector3(HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort())); break;
                                    case VertexElementFormat.Half4: vertex.Normal = new Vector3(HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort())); reader.ReadUShort(); break;
                                }
                            }
                            //else if (element.Usage == VertexElementUsage.Tangent)
                            //{
                            //    switch (element.Format)
                            //    {
                            //        case VertexElementFormat.Float3: vertex.Tangent = new Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()); break;
                            //        case VertexElementFormat.Float4: vertex.Tangent = new Vector3(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()); reader.ReadFloat(); break;
                            //        case VertexElementFormat.Half3: vertex.Tangent = new Vector3(HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort())); break;
                            //        case VertexElementFormat.Half4: vertex.Tangent = new Vector3(HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort())); reader.ReadUShort(); break;
                            //    }
                            //}
                            //else if (element.Usage == VertexElementUsage.Binormal)
                            //{
                            //    switch (element.Format)
                            //    {
                            //        case VertexElementFormat.Float3: vertex.Binormal = new Vector4(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(), 1.0f); break;
                            //        case VertexElementFormat.Float4: vertex.Binormal = new Vector4(reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat(), reader.ReadFloat()); break;
                            //        case VertexElementFormat.Half3: vertex.Binormal = new Vector4(HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort()), 1.0f); break;
                            //        case VertexElementFormat.Half4: vertex.Binormal = new Vector4(HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort()), HalfUtils.Unpack(reader.ReadUShort())); break;
                            //    }
                            //}
                            //else if (element.Usage == VertexElementUsage.BinormalSign)
                            //{
                            //    switch (element.Format)
                            //    {
                            //        case VertexElementFormat.Half: vertex.Binormal = new Vector4(vertex.Binormal.X, vertex.Binormal.Y, vertex.Binormal.Z, HalfUtils.Unpack(reader.ReadUShort())); break;
                            //        case VertexElementFormat.Float: vertex.Binormal = new Vector4(vertex.Binormal.X, vertex.Binormal.Y, vertex.Binormal.Z, reader.ReadFloat()); break;
                            //    }
                            //}
                            else
                            {
                                reader.Position += element.Size;
                            }
                        }

                        currentStride += element.Size;
                    }

                    vertices[v] = vertex;

                    if (currentStride < stream.VertexStride)
                        reader.Position += (stream.VertexStride - currentStride);
                }

                totalStride += stream.VertexStride;
            }
        }

        return vertices;
    }

    public void Dispose()
    {
        VertexBuffer.Dispose();
    }

    public void Render(D3D11.DeviceContext context, MeshRenderPath renderPath)
    {

        context.InputAssembler.PrimitiveTopology = PrimitiveType;
        context.InputAssembler.SetVertexBuffers(0, VertexBufferBinding);

        context.DrawIndexed(PrimitiveCount * 3, StartIndex, 0);
    }
}

public class MeshLodRenderable : BaseRenderable
{
    public List<MeshSectionRenderable> Sections { get; private set; } = [];

    private D3D11.Buffer indexBuffer;
    private SharpDX.DXGI.Format indexBufferFormat;

    private List<int> hitProxyIndices = [];

    public MeshLodRenderable(RenderCreateState state, MeshSetLod lod, bool isHitProxy)
    {
        byte[] chunkData = GetChunkData(lod);
        using (DataStream chunkStream = new DataStream((int)lod.IndexBufferSize, false, true))
        {
            chunkStream.Write(chunkData, (int)lod.VertexBufferSize, (int)lod.IndexBufferSize);
            chunkStream.Position = 0;

            indexBuffer = new D3D11.Buffer(state.Device, chunkStream, (int)lod.IndexBufferSize, D3D11.ResourceUsage.Default, D3D11.BindFlags.IndexBuffer, D3D11.CpuAccessFlags.None, D3D11.ResourceOptionFlags.None, lod.IndexUnitSize / 8);
            if (isHitProxy)
            {
                using (NativeReader reader = new NativeReader(new MemoryStream(chunkData)))
                {
                    reader.Position = lod.VertexBufferSize;
                    while (reader.Position < reader.Length)
                    {
                        int index = (lod.IndexUnitSize == 16)
                            ? reader.ReadUShort()
                            : reader.ReadInt();

                        hitProxyIndices.Add(index);
                    }
                }
            }
        }

        indexBufferFormat = (lod.IndexUnitSize == 16)
            ? SharpDX.DXGI.Format.R16_UInt
            : SharpDX.DXGI.Format.R32_UInt;

        foreach (MeshSetSection section in lod.Sections)
        {
            if (!lod.IsSectionRenderable(section))
                continue;

            if (section.VertexCount == 0)
                continue;

            MeshSectionRenderable renderSection = new MeshSectionRenderable(state, section, chunkData, isHitProxy);
            Sections.Add(renderSection);
        }
    }

    internal static byte[] GetChunkData(MeshSetLod lod)
    {
        ChunkAssetEntry entry = App.AssetManager.GetChunkEntry(lod.ChunkId);
        byte[] chunkData = lod.InlineData;
        if (entry != null)
            chunkData = new NativeReader(App.AssetManager.GetChunk(entry)).ReadToEnd();
        return chunkData;
    }

    public bool HitTest(Matrix transform, Ray hitTestRay, out Vector3 hitLocation)
    {
        foreach (MeshSectionRenderable section in Sections)
        {
            for (int i = 0; i < section.PrimitiveCount; i++)
            {
                Vector3 vertex1 = section.HitProxyVertices[hitProxyIndices[section.StartIndex + ((i * 3) + 0)]];
                Vector3 vertex2 = section.HitProxyVertices[hitProxyIndices[section.StartIndex + ((i * 3) + 1)]];
                Vector3 vertex3 = section.HitProxyVertices[hitProxyIndices[section.StartIndex + ((i * 3) + 2)]];
                vertex1 = Vector3.TransformCoordinate(vertex1, transform);
                vertex2 = Vector3.TransformCoordinate(vertex2, transform);
                vertex3 = Vector3.TransformCoordinate(vertex3, transform);
                bool intersects = hitTestRay.Intersects(ref vertex1, ref vertex2, ref vertex3, out hitLocation);
                if (intersects)
                {
                    return true;
                }
            }
        }

        hitLocation = Vector3.Zero;
        return false;
    }

    public void Dispose()
    {
        foreach (MeshSectionRenderable section in Sections)
            section.Dispose();

        indexBuffer.Dispose();
    }

    public override void Render(D3D11.DeviceContext context, MeshRenderPath renderPath)
    {
        context.InputAssembler.SetIndexBuffer(indexBuffer, indexBufferFormat, 0);
        foreach (MeshSectionRenderable section in Sections)
            section.Render(context, renderPath);
    }
}

/// <summary>
/// CPU-side copy of LOD0 triangles used for mouse picking. Built the first time a mesh is
/// ray-tested, instead of for every mesh at load time.
/// </summary>
internal sealed class MeshPickingData
{
    private sealed class Section
    {
        public int StartIndex;
        public int PrimitiveCount;
        public Vector3[] Positions;
    }

    private readonly List<Section> sections = new List<Section>();
    private readonly int[] indices;

    public MeshPickingData(MeshSetLod lod)
    {
        byte[] chunkData = MeshLodRenderable.GetChunkData(lod);

        List<int> indexList = new List<int>();
        using (NativeReader reader = new NativeReader(new MemoryStream(chunkData)))
        {
            reader.Position = lod.VertexBufferSize;
            long end = System.Math.Min(reader.Length, (long)lod.VertexBufferSize + lod.IndexBufferSize);
            while (reader.Position < end)
                indexList.Add(lod.IndexUnitSize == 16 ? reader.ReadUShort() : reader.ReadInt());
        }
        indices = indexList.ToArray();

        foreach (MeshSetSection section in lod.Sections)
        {
            if (!lod.IsSectionRenderable(section) || section.VertexCount == 0)
                continue;

            MeshVertex[] vertices = MeshSectionRenderable.ReadVertices(section, chunkData);
            Vector3[] positions = new Vector3[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
                positions[i] = vertices[i].Position;

            sections.Add(new Section
            {
                StartIndex = (int)section.StartIndex,
                PrimitiveCount = (int)section.PrimitiveCount,
                Positions = positions
            });
        }
    }

    public bool HitTest(Matrix transform, Ray hitTestRay, out Vector3 hitLocation)
    {
        foreach (Section section in sections)
        {
            for (int i = 0; i < section.PrimitiveCount; i++)
            {
                int i0 = section.StartIndex + (i * 3);
                if (i0 + 2 >= indices.Length)
                    break;

                int a = indices[i0], b = indices[i0 + 1], c = indices[i0 + 2];
                if (a >= section.Positions.Length || b >= section.Positions.Length || c >= section.Positions.Length)
                    continue;

                Vector3 vertex1 = Vector3.TransformCoordinate(section.Positions[a], transform);
                Vector3 vertex2 = Vector3.TransformCoordinate(section.Positions[b], transform);
                Vector3 vertex3 = Vector3.TransformCoordinate(section.Positions[c], transform);
                if (hitTestRay.Intersects(ref vertex1, ref vertex2, ref vertex3, out hitLocation))
                    return true;
            }
        }

        hitLocation = Vector3.Zero;
        return false;
    }
}

/// <summary>
/// GPU data for one mesh asset, shared by every proxy that uses the mesh.
///
/// The textured (MeshSetPlugin) mesh is built at load time. The untextured position/normal LODs
/// are only needed for the solid-colour view mode, and the picking triangles only when a mesh is
/// clicked, so both are built on first use. (Building them for every mesh at load took ~70% of
/// the render phase of SP_MainCity.) If the textured mesh cannot be built, the untextured LODs are
/// built immediately as the fallback.
/// </summary>
public class MeshRenderable : BaseRenderable
{
    public BoundingBox Bounds { get; private set; }
    public float[] LodDistances { get; private set; } = new float[6];
    public float CullScreenArea { get; private set; } = 0.0f;

    /// <summary>Diagnostics: time spent building the untextured (position/normal) LODs at load.</summary>
    public double UntexturedBuildMilliseconds { get; private set; }
    /// <summary>Diagnostics: time spent building the textured MeshSetPlugin mesh, including texture loads.</summary>
    public double TexturedBuildMilliseconds { get; private set; }

    /// <summary>
    /// 当材质加载成功时，使用 MeshSetPlugin 的带贴图渲染
    /// </summary>
    public MeshRenderMesh TexturedMesh { get; private set; }

    private static int lazyUntexturedBuilds;

    private readonly RenderCreateState createState;
    private readonly MeshSet meshSet;
    private readonly object lazyLock = new object();
    private List<MeshLodRenderable> untexturedLods;
    private MeshPickingData pickingData;
    private bool pickingFailed;
    private bool disposed;

    public MeshRenderable(RenderCreateState state, MeshSet meshSet, FrostySdk.Ebx.MeshLodGroup lodGroup,
        MeshMaterialCollection materials = null, ResAssetEntry meshResEntry = null)
    {
        createState = state;
        this.meshSet = meshSet;

        LodDistances[0] = lodGroup.Lod1Distance;
        LodDistances[1] = lodGroup.Lod2Distance;
        LodDistances[2] = lodGroup.Lod3Distance;
        LodDistances[3] = lodGroup.Lod4Distance;
        LodDistances[4] = lodGroup.Lod5Distance;
        LodDistances[5] = lodGroup.Lod6Distance;
        CullScreenArea = lodGroup.CullScreenArea;

        Bounds = new BoundingBox(
            new Vector3(meshSet.BoundingBox.min.x, meshSet.BoundingBox.min.y, meshSet.BoundingBox.min.z),
            new Vector3(meshSet.BoundingBox.max.x, meshSet.BoundingBox.max.y, meshSet.BoundingBox.max.z)
            );

        long diagTextured = System.Diagnostics.Stopwatch.GetTimestamp();
        if (materials != null && materials.Count > 0 && meshResEntry != null)
        {
            try
            {
                MeshSetPlugin.Resources.MeshSet texturedMeshSet = App.AssetManager.GetResAs<MeshSetPlugin.Resources.MeshSet>(meshResEntry);
                if (texturedMeshSet != null)
                {
                    MeshRenderSkeleton skeleton = new MeshRenderSkeleton();
                    TexturedMesh = new MeshRenderMesh(state, texturedMeshSet, materials, skeleton);
                }
            }
            catch (System.Exception ex)
            {
                LoadReport.MeshMaterialFailure("creating textured mesh", ex);
                TexturedMesh = null;
            }
        }
        TexturedBuildMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - diagTextured) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        if (TexturedMesh == null)
        {
            // fallback: untextured is the only way to draw this mesh, build it now
            long diagStart = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureUntexturedLods();
            UntexturedBuildMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - diagStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }
    }

    /// <summary>Kept for existing callers; builds the untextured LODs if they are not built yet.</summary>
    public List<MeshLodRenderable> Lods => EnsureUntexturedLods();

    public bool UseTexturedRendering => TexturedMesh != null;

    /// <summary>
    /// 是否实际使用带贴图渲染（需同时满足：有 TexturedMesh 且用户选项开启）
    /// </summary>
    public bool ShouldUseTexturedRendering =>
        UseTexturedRendering && LevelEditorRenderingOptions.UseTexturedModelRendering;

    /// <summary>Returns the LOD to draw, or null if the mesh has nothing drawable.</summary>
    public object GetLod(int index)
    {
        if (ShouldUseTexturedRendering)
        {
            return TexturedMesh.GetLod(index);
        }

        List<MeshLodRenderable> lods = EnsureUntexturedLods();
        if (lods.Count == 0)
            return null;
        if (index >= lods.Count)
            return lods[lods.Count - 1];
        return lods[index];
    }

    /// <summary>Ray test against LOD0 triangles (built on first call for this mesh).</summary>
    public bool HitTest(Matrix transform, Ray hitTestRay, out Vector3 hitLocation)
    {
        MeshPickingData data = EnsurePickingData();
        if (data == null)
        {
            // could not read the mesh: fall back to the bounding box test the caller already passed
            hitLocation = transform.TranslationVector;
            return true;
        }
        return data.HitTest(transform, hitTestRay, out hitLocation);
    }

    private List<MeshLodRenderable> EnsureUntexturedLods()
    {
        lock (lazyLock)
        {
            if (untexturedLods != null)
                return untexturedLods;

            List<MeshLodRenderable> lods = new List<MeshLodRenderable>();
            if (!disposed)
            {
                if (TexturedMesh != null)
                {
                    int n = System.Threading.Interlocked.Increment(ref lazyUntexturedBuilds);
                    if (n == 1)
                        LevelEditorLog.Info("Mesh", "Solid colour mode: building untextured meshes on demand. The first frames can be slow on large levels.");
                }

                foreach (MeshSetLod lod in meshSet.Lods)
                    lods.Add(new MeshLodRenderable(createState, lod, false));
            }
            untexturedLods = lods;
            return untexturedLods;
        }
    }

    private MeshPickingData EnsurePickingData()
    {
        lock (lazyLock)
        {
            if (pickingData != null || pickingFailed || disposed)
                return pickingData;

            try
            {
                if (meshSet.Lods.Count > 0)
                    pickingData = new MeshPickingData(meshSet.Lods[0]);
                else
                    pickingFailed = true;
            }
            catch (System.Exception ex)
            {
                pickingFailed = true;
                LevelEditorLog.Warning("Picking", "Could not read mesh triangles for picking; using bounding box", ex);
            }
            return pickingData;
        }
    }

    public void Dispose()
    {
        lock (lazyLock)
        {
            disposed = true;
            TexturedMesh?.Dispose();
            TexturedMesh = null;
            if (untexturedLods != null)
            {
                foreach (MeshLodRenderable lod in untexturedLods)
                    lod.Dispose();
                untexturedLods = null;
            }
            pickingData = null;
        }
    }
}
