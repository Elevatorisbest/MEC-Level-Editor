using SharpDX;
using System;
using System.Collections.Generic;
using System.IO;

namespace MeshSetPlugin.Fbx
{
    /// <summary>
    /// Writes a plain triangle mesh (positions, normals, tangents, binormals, one UV set) to an FBX
    /// file laid out the way <see cref="FBXImporter"/> reads it: one node per LOD named "lodN",
    /// each with one mesh node named after the mesh section to fill. Used by the level editor's
    /// shape builder, which generates geometry and imports it into a copied mesh with the normal
    /// importer (that handles every vertex format and the chunk/inline data).
    ///
    /// Values are written as given: no unit or axis conversion (the importer does none either).
    /// </summary>
    public static class SimpleFbxMeshWriter
    {
        public static void Write(string path, int lodCount, string sectionName,
            IList<Vector3> positions, IList<Vector3> normals, IList<Vector3> tangents, IList<Vector3> binormals,
            IList<Vector2> uvs, IList<int> indices)
        {
            if (positions.Count == 0 || indices.Count % 3 != 0)
                throw new ArgumentException("The mesh has no vertices or an incomplete triangle.");
            if (normals.Count != positions.Count || tangents.Count != positions.Count || binormals.Count != positions.Count || uvs.Count != positions.Count)
                throw new ArgumentException("Every vertex needs a normal, tangent, binormal and UV.");

            using (FbxManager manager = new FbxManager())
            {
                FbxIOSettings settings = new FbxIOSettings(manager, FbxIOSettings.IOSROOT);
                settings.SetBoolProp(FbxIOSettings.EXP_FBX_MATERIAL, false);
                settings.SetBoolProp(FbxIOSettings.EXP_FBX_TEXTURE, false);
                settings.SetBoolProp(FbxIOSettings.EXP_FBX_GLOBAL_SETTINGS, true);
                manager.SetIOSettings(settings);

                FbxScene scene = new FbxScene(manager, "");
                for (int lod = 0; lod < Math.Max(1, lodCount); lod++)
                {
                    FbxNode lodNode = new FbxNode(scene, "lod" + lod);
                    lodNode.AddChild(CreateMeshNode(scene, sectionName, positions, normals, tangents, binormals, uvs, indices));
                    scene.RootNode.AddChild(lodNode);
                }

                string directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                using (FbxExporter exporter = new FbxExporter(manager, ""))
                {
                    exporter.SetFileExportVersion("FBX201300");
                    int format = -1;
                    for (int i = 0; i < manager.IOPluginRegistry.WriterFormatCount; i++)
                    {
                        if (manager.IOPluginRegistry.GetWriterFormatDescription(i).Contains("binary"))
                        {
                            format = i;
                            break;
                        }
                    }
                    if (!exporter.Initialize(path, format, settings))
                        throw new IOException("The FBX writer could not open " + path);
                    if (!exporter.Export(scene))
                        throw new IOException("The FBX writer could not write " + path);
                }
            }
        }

        private static FbxNode CreateMeshNode(FbxScene scene, string name,
            IList<Vector3> positions, IList<Vector3> normals, IList<Vector3> tangents, IList<Vector3> binormals,
            IList<Vector2> uvs, IList<int> indices)
        {
            FbxNode node = new FbxNode(scene, name);
            FbxMesh mesh = new FbxMesh(scene, name);

            mesh.InitControlPoints(positions.Count);
            IntPtr points = mesh.GetControlPoints();
            unsafe
            {
                double* ptr = (double*)points;
                for (int i = 0; i < positions.Count; i++)
                {
                    ptr[i * 4 + 0] = positions[i].X;
                    ptr[i * 4 + 1] = positions[i].Y;
                    ptr[i * 4 + 2] = positions[i].Z;
                    ptr[i * 4 + 3] = 1.0;
                }
            }

            FbxLayerElementNormal normalElement = new FbxLayerElementNormal(mesh, "") { MappingMode = EMappingMode.eByControlPoint, ReferenceMode = EReferenceMode.eDirect };
            FbxLayerElementTangent tangentElement = new FbxLayerElementTangent(mesh, "") { MappingMode = EMappingMode.eByControlPoint, ReferenceMode = EReferenceMode.eDirect };
            FbxLayerElementBinormal binormalElement = new FbxLayerElementBinormal(mesh, "") { MappingMode = EMappingMode.eByControlPoint, ReferenceMode = EReferenceMode.eDirect };
            FbxLayerElementUV uvElement = new FbxLayerElementUV(mesh, "UVChannel_1") { MappingMode = EMappingMode.eByControlPoint, ReferenceMode = EReferenceMode.eDirect };
            // white vertex colours: the importer writes 0 (black, transparent) for colour channels
            // the template mesh declares but the FBX does not have
            FbxLayerElementVertexColor color0 = new FbxLayerElementVertexColor(mesh, "Color0") { MappingMode = EMappingMode.eByControlPoint, ReferenceMode = EReferenceMode.eDirect };
            FbxLayerElementVertexColor color1 = new FbxLayerElementVertexColor(mesh, "Color1") { MappingMode = EMappingMode.eByControlPoint, ReferenceMode = EReferenceMode.eDirect };
            for (int i = 0; i < positions.Count; i++)
            {
                normalElement.DirectArray.Add(normals[i].X, normals[i].Y, normals[i].Z);
                tangentElement.DirectArray.Add(tangents[i].X, tangents[i].Y, tangents[i].Z);
                binormalElement.DirectArray.Add(binormals[i].X, binormals[i].Y, binormals[i].Z);
                uvElement.DirectArray.Add(uvs[i].X, uvs[i].Y);
                color0.DirectArray.Add(1.0, 1.0, 1.0, 1.0);
                color1.DirectArray.Add(1.0, 1.0, 1.0, 1.0);
            }

            for (int i = 0; i < indices.Count; i += 3)
            {
                mesh.BeginPolygon(0);
                mesh.AddPolygon(indices[i]);
                mesh.AddPolygon(indices[i + 1]);
                mesh.AddPolygon(indices[i + 2]);
                mesh.EndPolygon();
            }

            FbxLayer layer = mesh.GetLayer(0);
            if (layer == null)
            {
                mesh.CreateLayer();
                layer = mesh.GetLayer(0);
            }
            layer.SetNormals(normalElement);
            layer.SetTangents(tangentElement);
            layer.SetBinormals(binormalElement);
            layer.SetUVs(uvElement);
            layer.SetVertexColors(color0);
            if (mesh.GetLayer(1) == null)
                mesh.CreateLayer();
            mesh.GetLayer(1).SetVertexColors(color1);

            mesh.BuildMeshEdgeArray();
            node.SetNodeAttribute(mesh);
            return node;
        }
    }
}
