using Frosty.Core.Handlers;
using Frosty.Core.Mod;
using Frosty.Hash;
using FrostySdk;
using FrostySdk.IO;
using FrostySdk.Managers.Entries;
using System;
using System.Collections.Generic;
using System.IO;

namespace Frosty.Core.IO
{
    public sealed class FrostyModWriter : NativeWriter
    {
        public class Manifest
        {
            public int Count => objects.Count;

            private Dictionary<Sha1, int> sha1Entries = new Dictionary<Sha1, int>();
            private List<object> objects = new List<object>();

            public Manifest()
            {
            }

            public int Add(byte[] data)
            {
                objects.Add(data);
                return objects.Count - 1;
            }

            public int Add(Sha1 sha1, byte[] data)
            {
                if (sha1Entries.ContainsKey(sha1))
                    return sha1Entries[sha1];

                objects.Add(data);
                sha1Entries.Add(sha1, objects.Count - 1);

                return objects.Count - 1;
            }

            public void Write(NativeWriter writer)
            {
                long dataOffset = writer.Position + (objects.Count * 16);
                long currentOffset = 0;

                foreach (object obj in objects)
                {
                    // write offset
                    writer.Write(currentOffset);

                    long dataSize = 0;
                    long pos = writer.Position;
                    writer.Position = dataOffset + currentOffset;

                    byte[] data = (byte[])obj;
                    writer.Write(data);
                    dataSize = data.Length;

                    writer.Position = pos;

                    // write size
                    writer.Write(dataSize);
                    currentOffset += dataSize;
                }
            }
        }

        private sealed class EmbeddedResource : EditorModResource
        {
            public override ModResourceType Type => ModResourceType.Embedded;
            public EmbeddedResource(string inName, byte[] data, Manifest manifest)
            {
                name = inName;
                if (data != null)
                {
                    resourceIndex = manifest.Add(data);
                    size = data.Length;
                }
            }
        }

        private sealed class BundleResource : EditorModResource
        {
            public override ModResourceType Type => ModResourceType.Bundle;
            private int superBundleName;

            public BundleResource(BundleEntry entry, Manifest manifest)
            {
                name = entry.Name.ToLower();
                superBundleName = Fnv1a.HashString(App.AssetManager.GetSuperBundle(entry.SuperBundleId).Name.ToLower());
            }

            public override void Write(NativeWriter writer)
            {
                base.Write(writer);
                writer.WriteNullTerminatedString(name);
                writer.Write(superBundleName);
            }
        }

        private sealed class EbxResource : EditorModResource
        {
            public override ModResourceType Type => ModResourceType.Ebx;
            public EbxResource(EbxAssetEntry entry, Manifest manifest)
                : base(entry)
            {
                CompressionType compressType = (ProfilesLibrary.DataVersion == (int)ProfileVersion.Fifa18) ? CompressionType.ZStd : CompressionType.Default;
                byte[] data = null;

                name = entry.Name.ToLower();
                if (entry.HasModifiedData)
                {
                    userData = entry.ModifiedEntry.UserData;
                    using (EbxBaseWriter ebxWriter = EbxBaseWriter.CreateWriter(new MemoryStream()))
                    {
                        ebxWriter.WriteAsset(entry.ModifiedEntry.DataObject as EbxAsset);

                        size = ebxWriter.Length;
                        data = Utils.CompressFile(ebxWriter.ToByteArray(), compressionOverride: compressType);

                        resourceIndex = manifest.Add(data);
                        sha1 = Utils.GenerateSha1(data);
                    }
                }
            }
        }

        private sealed class ResResource : EditorModResource
        {
            public override ModResourceType Type => ModResourceType.Res;

            private uint resType;
            private ulong resRid;
            private byte[] resMeta;

            public ResResource(ResAssetEntry entry, Manifest manifest)
                : base(entry)
            {
                name = entry.Name.ToLower();
                if (entry.HasModifiedData)
                {
                    sha1 = entry.ModifiedEntry.Sha1;
                    resourceIndex = manifest.Add(entry.ModifiedEntry.Sha1, entry.ModifiedEntry.Data);
                    size = entry.ModifiedEntry.OriginalSize;

                    resType = entry.ResType;
                    resRid = entry.ResRid;
                    resMeta = (entry.ModifiedEntry.ResMeta != null) ? entry.ModifiedEntry.ResMeta : entry.ResMeta;
                    userData = entry.ModifiedEntry.UserData;

                    flags = (byte)((entry.IsInline) ? 1 : 0);
                }
            }

            public override void Write(NativeWriter writer)
            {
                base.Write(writer);

                writer.Write(resType);
                writer.Write(resRid);
                writer.Write((resMeta != null) ? resMeta.Length : 0);
                if (resMeta != null)
                    writer.Write(resMeta);
            }
        }

        private sealed class ChunkResource : EditorModResource
        {
            public override ModResourceType Type => ModResourceType.Chunk;

            private uint rangeStart;
            private uint rangeEnd;
            private uint logicalOffset;
            private uint logicalSize;
            private int h32;
            private int firstMip;

            /// <param name="textureChunks">
            /// For a chunk that is only added to bundles: the texture it belongs to, when there is
            /// one (see <see cref="TextureChunkInfo"/>). May be null.
            /// </param>
            public ChunkResource(ChunkAssetEntry entry, Manifest manifest, Func<Guid, TextureChunkInfo?> textureChunks = null)
                : base(entry)
            {
                name = entry.Id.ToString();
                if (entry.HasModifiedData)
                {
                    sha1 = entry.ModifiedEntry.Sha1;
                    resourceIndex = manifest.Add(entry.ModifiedEntry.Sha1, entry.ModifiedEntry.Data);
                    size = entry.ModifiedEntry.OriginalSize;

                    rangeStart = entry.ModifiedEntry.RangeStart;
                    rangeEnd = entry.ModifiedEntry.RangeEnd;
                    logicalOffset = entry.ModifiedEntry.LogicalOffset;
                    logicalSize = entry.ModifiedEntry.LogicalSize;
                    h32 = entry.ModifiedEntry.H32;
                    firstMip = entry.ModifiedEntry.FirstMip;
                    userData = entry.ModifiedEntry.UserData;

                    flags = (byte)((entry.IsInline) ? 1 : 0);
                    flags |= (byte)((entry.ModifiedEntry.AddToChunkBundle) ? 1 << 1 : 0);

                    // add special chunks bundle
                    if (entry.ModifiedEntry.AddToChunkBundle)
                    {
                        // MustAddChunks should add chunks, so seems redundant to check for it here,
                        // as the chunk should already be an added chunk

                        if (ProfilesLibrary.MustAddChunks || entry.IsAdded)
                            AddBundle("chunks");
                    }
                }
                else
                {
                    // special chunks bundle
                    if (App.FileSystemManager.GetManifestChunk(new Guid(name)) != null)
                    {
                        // not required?
                        flags |= 0x04;
                    }

                    // @todo: calculate RangeStart/End for texture chunks added to bundles, where they are not
                    //        stored in the bundle (ie. Manifest layout)

                    h32 = entry.H32;
                    firstMip = entry.FirstMip;

                    // A texture chunk added to a bundle without changing it (e.g. a placed prop made
                    // available in another level) has no H32 or FirstMip here: they are not kept in
                    // the editor's cache or in the project for unchanged chunks. Without them the new
                    // bundle lists the texture's data as if it held every mip from 0, with no range
                    // of the streamed part, and the game fails to create the texture
                    // (CreateTexture2D: E_INVALIDARG). Fill them in from the texture, the same way
                    // texture import does (H32 = hash of the texture's name, FirstMip = its
                    // FirstMip); the mod executor then also writes the chunk's bundled range.
                    if (entry.AddedBundles.Count > 0 && (h32 == 0 || firstMip == -1) && textureChunks != null)
                    {
                        TextureChunkInfo? texture = textureChunks(entry.Id);
                        if (texture.HasValue)
                        {
                            if (h32 == 0)
                                h32 = texture.Value.H32;
                            if (firstMip == -1)
                                firstMip = texture.Value.FirstMip;
                            FilledFromTexture = true;
                        }
                    }
                }
            }

            /// <summary>True when H32 / FirstMip were taken from the chunk's texture.</summary>
            public bool FilledFromTexture { get; }

            public override void Write(NativeWriter writer)
            {
                base.Write(writer);

                writer.Write(rangeStart);
                writer.Write(rangeEnd);
                writer.Write(logicalOffset);
                writer.Write(logicalSize);
                writer.Write(h32);
                writer.Write(firstMip);
            }
        }

        /// <summary>What a texture's data chunk needs in a bundle it is added to.</summary>
        public struct TextureChunkInfo
        {
            public int H32;
            public int FirstMip;
            public string TextureName;
        }

        /// <summary>
        /// Texture data chunks of the textures that are added to bundles in this project, by chunk
        /// id. Built when the first such chunk is written; reading a texture resource also loads its
        /// pixel data, so only textures with bundle additions are read.
        /// </summary>
        private Dictionary<Guid, TextureChunkInfo> textureChunks;

        private TextureChunkInfo? FindTextureChunk(Guid chunkId)
        {
            if (textureChunks == null)
            {
                textureChunks = new Dictionary<Guid, TextureChunkInfo>();
                foreach (ResAssetEntry res in App.AssetManager.EnumerateRes(modifiedOnly: true))
                {
                    if (res.ResType != (uint)ResourceType.Texture || res.AddedBundles.Count == 0)
                        continue;
                    try
                    {
                        using (FrostySdk.Resources.Texture texture = App.AssetManager.GetResAs<FrostySdk.Resources.Texture>(res))
                        {
                            if (texture == null || texture.ChunkId == Guid.Empty)
                                continue;
                            textureChunks[texture.ChunkId] = new TextureChunkInfo
                            {
                                H32 = Fnv1.HashString(res.Name.ToLower()),
                                FirstMip = texture.FirstMip,
                                TextureName = res.Name
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        App.Logger.LogWarning("{0}", $"Mod writer: could not read texture {res.Name} for its chunk's bundle data: {ex.Message}");
                    }
                }
            }
            return textureChunks.TryGetValue(chunkId, out TextureChunkInfo info) ? info : (TextureChunkInfo?)null;
        }

        public Manifest ResourceManifest => manifest;

        private ModSettings overrideSettings;
        private Manifest manifest = new Manifest();
        private List<BaseModResource> resources = new List<BaseModResource>();

        public FrostyModWriter(Stream inStream, ModSettings inOverrideSettings = null)
            : base(inStream)
        {
            overrideSettings = inOverrideSettings;
        }

        public void WriteProject(FrostyProject project, bool editorLaunch = false)
        {
            Write(FrostyMod.Magic);
            Write(FrostyMod.Version);
            Write(0xDEADBEEFDEADBEEF);
            Write(0xDEADBEEF);
            Write(ProfilesLibrary.ProfileName);
            Write(App.FileSystemManager.Head);

            ModSettings settings = overrideSettings ?? project.ModSettings;

            WriteNullTerminatedString(settings.Title);
            WriteNullTerminatedString(settings.Author);
            WriteNullTerminatedString(settings.Category);
            WriteNullTerminatedString(settings.Version);
            WriteNullTerminatedString(settings.Description);
            WriteNullTerminatedString(settings.Link);

            AddResource(new EmbeddedResource("Icon", settings.Icon, manifest));
            for (int i = 0; i < 4; i++)
                AddResource(new EmbeddedResource("Screenshot" + i.ToString(), settings.GetScreenshot(i), manifest));

            // @todo: superbundles

            // added bundles
            bool addedBundlesAllowed = Config.Get<bool>("AddedBundlesFbmod", true);
            List<int> disallowedBundles = new List<int>();
            foreach (BundleEntry bentry in App.AssetManager.EnumerateBundles(modifiedOnly: true))
            {
                if (!bentry.Added)
                    continue;

                if (addedBundlesAllowed)
                    AddResource(new BundleResource(bentry, manifest));
                else
                    disallowedBundles.Add(HashBundle(bentry));
            }

            int HashBundle(BundleEntry bentry)
            {
                int hash = Fnv1.HashString(bentry.Name.ToLower());

                if (bentry.Name.Length == 8 && int.TryParse(bentry.Name, System.Globalization.NumberStyles.HexNumber, null, out int tmp))
                    hash = tmp;

                return hash;
            }

            // ebx
            foreach (EbxAssetEntry entry in App.AssetManager.EnumerateEbx(modifiedOnly: true))
            {
                if (entry.IsDirectlyModified)
                {
                    // ignore transient only edits (such as id fields)
                    if (entry.HasModifiedData && entry.ModifiedEntry.IsTransientModified)
                        continue;

                    //Don't bother writing assets if they are not in whitelisted bundles, when launching from the editor.
                    if (editorLaunch && App.WhitelistedBundles.Count != 0 && !(entry.Bundles.Count == 0 && !entry.IsAdded))
                    {
                        List<int> totalBundles = new List<int>(entry.Bundles);
                        totalBundles.AddRange(entry.AddedBundles);

                        bool hasWhitelistedBundle = false;
                        foreach (int bunId in totalBundles)
                        {
                            if (App.WhitelistedBundles.Contains(HashBundle(App.AssetManager.GetBundleEntry(bunId))) || (addedBundlesAllowed && App.AssetManager.GetBundleEntry(bunId).Added))
                            {
                                hasWhitelistedBundle = true;
                                break;
                            }
                        }
                        if (!hasWhitelistedBundle)
                            continue;
                    }

                    if (entry.HasModifiedData)
                    {
                        ICustomActionHandler actionHandler = App.PluginManager.GetCustomHandler(entry.Type);
                        if (actionHandler != null && !entry.IsAdded)
                        {
                            // use custom action handler to save asset to mod
                            actionHandler.SaveToMod(this, entry);
                        }
                        else
                        {
                            // save as regular asset
                            AddResource(new EbxResource(entry, manifest));
                        }
                    }
                    else
                    {
                        AddResource(new EbxResource(entry, manifest));
                    }
                }
            }

            // res
            foreach (ResAssetEntry entry in App.AssetManager.EnumerateRes(modifiedOnly: true))
            {
                if (entry.IsDirectlyModified)
                {
                    if (entry.HasModifiedData)
                    {
                        ICustomActionHandler actionHandler = App.PluginManager.GetCustomHandler((ResourceType)entry.ResType);
                        if (actionHandler != null && !entry.IsAdded)
                        {
                            // use custom action handler to save resource to mod
                            actionHandler.SaveToMod(this, entry);
                        }
                        else
                        {
                            // save as regular resource
                            AddResource(new ResResource(entry, manifest));
                        }
                    }
                    else
                    {
                        AddResource(new ResResource(entry, manifest));
                    }
                }
            }

            // chunks
            int texturesFilled = 0;
            foreach (ChunkAssetEntry entry in App.AssetManager.EnumerateChunks(modifiedOnly: true))
            {
                // chunks dont require custom handlers (except for the special case of Legacy). As chunks should never be modified
                // directly, but instead as part of an ebx or res modification

                if (entry.IsDirectlyModified)
                {
                    ChunkResource chunkResource = new ChunkResource(entry, manifest, FindTextureChunk);
                    if (chunkResource.FilledFromTexture)
                        texturesFilled++;
                    AddResource(chunkResource);
                }
            }
            if (texturesFilled > 0)
                App.Logger.Log("{0}", $"Mod writer: completed the bundle data (H32, first mip) of {texturesFilled} texture chunk(s) added to bundles");

            // legacy custom action handler
            ICustomAssetCustomActionHandler tmpHandler = new LegacyCustomActionHandler();
            tmpHandler.SaveToMod(this);

            // write resources
            Write(resources.Count);
            foreach (EditorModResource resource in resources)
            {
                resource.Write(this);
            }

            // write manifest + data
            long manifestOffset = Position;
            manifest.Write(this);

            // finally update manifest offset in header
            Position = 12;
            Write(manifestOffset);
            Write(manifest.Count);
        }

        public void AddResource(BaseModResource resource)
        {
            resources.Add(resource);
        }
    }
}