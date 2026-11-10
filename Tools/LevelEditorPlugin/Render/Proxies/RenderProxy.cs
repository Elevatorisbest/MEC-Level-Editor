using Frosty.Core.Screens;
using Frosty.Core.Viewport;
using LevelEditorPlugin.Entities;
using SharpDX;
using SharpDX.Direct3D11;
using System;

namespace LevelEditorPlugin.Render.Proxies;

public abstract class RenderProxy : MeshRenderBase
{
    public float CurrentDistanceToCamera { get; set; }
    public Matrix Transform { get; protected set; }
    public BoundingBox BoundingBox
    {
        get => boundingBox;
        protected set
        {
            boundingBox = value;
            // Subclasses set the box directly without calling base.RecalculateBoundingBox, so the
            // cached sphere is invalidated here (it used to stay stale after an object moved).
            boundingSphereDirty = true;
            Grid?.OnBoundsChanged(this, value);
        }
    }
    public Entity OwnerEntity { get; protected set; }

    private BoundingBox boundingBox;
    private BoundingSphere cachedBoundingSphere;

    // Culling grid bookkeeping (see ProxyGrid). Only touched by the grid.
    internal ProxyGrid Grid;
    internal BoundingBox GridBounds;
    internal Vector3 GridTranslation;
    internal float GridRadius;
    internal float GridReach;
    internal bool GridDynamic;
    private bool boundingSphereDirty = true;

    /// <summary>
    /// 每个 Proxy 缓存一次包围球，避免在 CollectMeshInstances 中每帧重复从包围盒推导。
    /// </summary>
    public BoundingSphere BoundingSphere
    {
        get
        {
            if (boundingSphereDirty)
            {
                cachedBoundingSphere = BoundingSphere.FromBox(BoundingBox);
                boundingSphereDirty = false;
            }
            return cachedBoundingSphere;
        }
    }

    public RenderProxy(ISpatialEntity owner)
    {
        OwnerEntity = owner as Entity;
        Transform = owner.GetTransform();
    }

    public override void Render(DeviceContext context, MeshRenderPath renderPath)
    {
        throw new NotImplementedException();
    }

    /// <summary>True if <see cref="Update"/> must run every frame, not only after transform changes.</summary>
    public virtual bool NeedsPerFrameUpdate => false;

    /// <summary>
    /// Minimum screen size below which <see cref="ShouldRender"/> always returns false (used by the
    /// culling grid; must never be larger than the real threshold).
    /// </summary>
    public virtual float MinScreenSize => 0.0f;

    public virtual void Update(RenderCreateState state)
    {
        if (OwnerEntity.RequiresTransformUpdate)
        {
            Transform = (OwnerEntity as ISpatialEntity).GetTransform();
            RecalculateBoundingBox();

            OwnerEntity.RequiresTransformUpdate = false;
        }
    }

    public virtual void RecalculateBoundingBox()
    {
        // do nothing
        boundingSphereDirty = true;
    }

    public virtual bool HitTest(Ray hitTestRay, out Vector3 hitLocation)
    {
        hitLocation = Transform.TranslationVector;
        return true;
    }

    public virtual bool ShouldRender(float distToCamera, float screenSize)
    {
        return true;
    }

    public virtual MeshRenderInstance GetInstance(float distToCamera)
    {
        return new MeshRenderInstance() { RenderMesh = this, Transform = Transform };
    }

    public virtual void SetSelected(RenderCreateState state, bool newSelected)
    {
    }

    /// <summary>
    /// Box drawn around this proxy when its entity is selected (see Render/SelectionBoxRenderer.cs):
    /// <paramref name="localBounds"/> in the space that <paramref name="world"/> places in the world.
    /// The default is the world-aligned bounding box. Returns false when no box should be drawn.
    /// </summary>
    public virtual bool GetSelectionBounds(out BoundingBox localBounds, out Matrix world)
    {
        localBounds = BoundingBox;
        world = Matrix.Identity;
        return true;
    }

    public abstract void Dispose();
}
