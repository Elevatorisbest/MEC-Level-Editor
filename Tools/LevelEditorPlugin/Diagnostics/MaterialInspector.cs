using Frosty.Core;
using Frosty.Core.Viewport;
using FrostySdk.IO;
using LevelEditorPlugin.Render;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LevelEditorPlugin.Diagnostics;

/// <summary>
/// Writes the materials of a selected mesh to the session log: shader graph, texture parameters
/// (with the texture asset each points to) and vector parameters. Used to work out why a surface
/// renders wrongly in the simplified viewport shader.
/// </summary>
public static class MaterialInspector
{
    private static readonly HashSet<MeshRenderable> alreadyLogged = new HashSet<MeshRenderable>();

    public static void LogSelection(string entityName, IEnumerable<MeshRenderable> meshes)
    {
        foreach (MeshRenderable mesh in meshes)
        {
            if (mesh == null)
                continue;

            lock (alreadyLogged)
            {
                // each mesh once per session; selecting it again does not repeat the block
                if (!alreadyLogged.Add(mesh))
                {
                    LevelEditorLog.Info("Selection", $"{entityName}: mesh {mesh.DebugMeshName} (materials already logged above)");
                    continue;
                }
            }

            LevelEditorLog.WriteBlock($"Selected {entityName}", Describe(mesh));
        }
    }

    private static string Describe(MeshRenderable mesh)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine($"Mesh: {mesh.DebugMeshName ?? "(unknown)"}");
        sb.AppendLine($"Textured path: {(mesh.UseTexturedRendering ? "yes" : "no (untextured fallback)")}");

        if (mesh.DebugMaterials == null)
        {
            sb.AppendLine("Materials: not available (material collection could not be read)");
            return sb.ToString().TrimEnd();
        }

        DescribeSections(mesh, sb);

        int index = 0;
        foreach (MeshMaterial material in mesh.DebugMaterials)
        {
            sb.AppendLine($"Material {index++}: shader {ShaderName(material)}");
            try
            {
                foreach (dynamic parameter in material.TextureParameters)
                {
                    string name = parameter.ParameterName;
                    FrostySdk.Ebx.PointerRef value = parameter.Value;
                    string target = value.Type == PointerRefType.External
                        ? (App.AssetManager.GetEbxEntry(value.External.FileGuid)?.Name ?? value.External.FileGuid.ToString())
                        : "(none)";
                    sb.AppendLine($"    texture {name} = {target}");
                }
                foreach (dynamic parameter in material.VectorParameters)
                {
                    string name = parameter.ParameterName;
                    dynamic v = parameter.Value;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "    vector  {0} = ({1:0.###}, {2:0.###}, {3:0.###}, {4:0.###})", name, (float)v.x, (float)v.y, (float)v.z, (float)v.w));
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"    (could not read parameters: {ex.GetType().Name}: {ex.Message})");
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Vertex data and bound textures of each LOD0 section (texture array investigation).</summary>
    private static void DescribeSections(MeshRenderable mesh, StringBuilder sb)
    {
        try
        {
            MeshSetPlugin.Render.MeshRenderLod lod = mesh.TexturedMesh?.GetLod(0);
            if (lod == null)
                return;
            int i = 0;
            foreach (MeshSetPlugin.Render.MeshRenderSection section in lod.Sections)
            {
                List<string> elements = new List<string>();
                foreach (FrostySdk.GeometryDeclarationDesc.Element element in section.MeshSection.GeometryDeclDesc[0].Elements)
                {
                    if (element.Usage != FrostySdk.VertexElementUsage.Unknown)
                        elements.Add($"{element.Usage}:{element.Format}");
                }
                sb.AppendLine($"Section {i++}: material {section.MeshSection.MaterialId}, {section.MeshSection.VertexCount} vertices, vertex data: {string.Join(", ", elements)}");

                List<string> textures = new List<string>();
                int slot = 1;
                foreach (SharpDX.Direct3D11.ShaderResourceView srv in section.PixelTextures)
                {
                    if (srv != null)
                    {
                        var d = srv.Description;
                        string shape = d.Dimension == SharpDX.Direct3D.ShaderResourceViewDimension.Texture2DArray
                            ? $"array of {d.Texture2DArray.ArraySize}" : d.Dimension.ToString();
                        textures.Add($"t{slot}={shape} {d.Format}");
                    }
                    slot++;
                }
                sb.AppendLine($"    bound textures: {string.Join(", ", textures)}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Sections: could not read ({ex.GetType().Name}: {ex.Message})");
        }
    }

    private static string ShaderName(MeshMaterial material)
    {
        if (material.Shader.Type == PointerRefType.External)
            return App.AssetManager.GetEbxEntry(material.Shader.External.FileGuid)?.Name ?? material.Shader.External.FileGuid.ToString();
        return material.Shader.Type == PointerRefType.Internal ? "(internal)" : "(none; the fallback shader skips this material)";
    }
}
