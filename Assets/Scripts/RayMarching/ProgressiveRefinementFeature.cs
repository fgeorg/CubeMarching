using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// Progressive (multiframe) refinement for SDF ray marching.
// Per-camera buffers track ray march distances across frames.
// Cache is invalidated when the camera moves or the SDF scene changes.
//
// Setup:
//   1. Add this feature to your URP ForwardRenderer asset.
//   2. Enable _PROGRESSIVE_REFINEMENT_ON on the SDF material.
//   3. The SDF shader must use LightMode = "SdfMrt".
[ExecuteInEditMode]
public class ProgressiveRefinementFeature : ScriptableRendererFeature
{
    const int BufferCount = 2;

    class CameraState
    {
        public RTHandle[] handles = new RTHandle[BufferCount];
        public int        bufferIndex;
        public bool       sceneDirty;      // set by OnSdfSceneRebuilt, consumed in AddRenderPasses
        public Matrix4x4  prevViewMatrix;  // used to detect camera movement
        // Per-camera instances — a shared instance's Setup() gets overwritten because
        // AddRenderPasses runs for all cameras before RecordRenderGraph runs for any.
        public SdfMrtPass sdfMrtPass;
    }

    readonly Dictionary<int, CameraState> m_CameraStates = new Dictionary<int, CameraState>();

    public override void Create()
    {
    }

    void OnEnable()
    {
        SdfScene.Rebuilt += OnSdfSceneRebuilt;
    }

    void OnDisable()
    {
        SdfScene.Rebuilt -= OnSdfSceneRebuilt;
    }

    void OnSdfSceneRebuilt()
    {
        foreach (CameraState state in m_CameraStates.Values)
        {
            state.sceneDirty = true;
        }
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        Camera cam   = renderingData.cameraData.camera;
        int    camId = cam.GetInstanceID();

        if (!m_CameraStates.TryGetValue(camId, out CameraState state))
        {
            SdfMrtPass sdfMrtPass = new SdfMrtPass();
            sdfMrtPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            state = new CameraState { sdfMrtPass = sdfMrtPass };
            m_CameraStates[camId] = state;
        }

        // Realloc per-camera handles if resolution changed.
        RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
        desc.msaaSamples       = 1;
        desc.depthBufferBits   = 0;
        desc.graphicsFormat    = GraphicsFormat.R32_SFloat;
        desc.enableRandomWrite = true; // both handles need UAV (each acts as curr and prev)

        for (int i = 0; i < BufferCount; i++)
        {
            RTHandle h = state.handles[i];
            RenderingUtils.ReAllocateHandleIfNeeded(ref h, desc,
                FilterMode.Point, TextureWrapMode.Clamp, name: $"_dist_buffer_{i}");
            state.handles[i] = h;
        }

        // Advance index: last frame's curr becomes this frame's prev.
        int prevIdx = state.bufferIndex;
        state.bufferIndex = (state.bufferIndex + 1) % BufferCount;
        int currIdx = state.bufferIndex;

        Matrix4x4 currViewMatrix = cam.worldToCameraMatrix;
        bool invalidate      = currViewMatrix != state.prevViewMatrix || state.sceneDirty;
        state.sceneDirty     = false;
        state.prevViewMatrix = currViewMatrix;

        state.sdfMrtPass.Setup(state.handles[currIdx], state.handles[prevIdx], invalidate);

        renderer.EnqueuePass(state.sdfMrtPass);
    }

    protected override void Dispose(bool disposing)
    {
        foreach (CameraState state in m_CameraStates.Values)
        {
            foreach (RTHandle h in state.handles)
            {
                h?.Release();
            }
        }
        m_CameraStates.Clear();
    }

    // Binds _PrevSdfDistTex (last frame's distances), draws SDF, UAV-writes
    // this frame's distances into currHandle. Reprojection uses UNITY_MATRIX_I_VP.
    class SdfMrtPass : ScriptableRenderPass
    {
        static readonly ShaderTagId s_SdfMrtTag       = new("SdfMrt");
        static readonly int         s_PrevSdfDistTexId = Shader.PropertyToID("_PrevSdfDistTex");

        RTHandle m_CurrHandle;
        RTHandle m_PrevHandle;
        bool     m_Invalidate;

        public void Setup(RTHandle currHandle, RTHandle prevHandle, bool invalidate)
        {
            m_CurrHandle = currHandle;
            m_PrevHandle = prevHandle;
            m_Invalidate = invalidate;
        }

        class PassData
        {
            public RendererListHandle rendererList;
            public TextureHandle      colorTarget;
            public TextureHandle      depthTarget;
            public RenderTexture      currRT;
            public TextureHandle      prevDist;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            var resourceData  = frameData.Get<UniversalResourceData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var cameraData    = frameData.Get<UniversalCameraData>();

            if (!resourceData.activeColorTexture.IsValid() || !resourceData.cameraDepth.IsValid())
            {
                return;
            }

            SortingSettings sortSettings = new SortingSettings(cameraData.camera)
            {
                criteria = SortingCriteria.CommonTransparent
            };
            DrawingSettings drawSettings = new DrawingSettings(s_SdfMrtTag, sortSettings)
            {
                enableDynamicBatching = renderingData.supportsDynamicBatching,
                enableInstancing      = true,
                perObjectData         = PerObjectData.ReflectionProbes | PerObjectData.OcclusionProbe
            };
            FilteringSettings filterSettings = new FilteringSettings(RenderQueueRange.transparent);

            RendererListParams listParams = new RendererListParams(
                renderingData.cullResults, drawSettings, filterSettings);
            RendererListHandle sdfList = renderGraph.CreateRendererList(listParams);

            TextureHandle currDistHandle = renderGraph.ImportTexture(m_CurrHandle);

            // Use blackTexture when invalidated so the shader doesn't reproject stale distances.
            TextureHandle prevDistHandle = m_Invalidate
                ? renderGraph.defaultResources.blackTexture
                : renderGraph.ImportTexture(m_PrevHandle);

            using (var builder = renderGraph.AddUnsafePass<PassData>("SdfMrtPass", out PassData passData))
            {
                passData.rendererList = sdfList;
                passData.colorTarget  = resourceData.activeColorTexture;
                passData.depthTarget  = resourceData.cameraDepth;
                passData.currRT       = m_CurrHandle.rt;
                passData.prevDist     = prevDistHandle;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(resourceData.cameraDepth, AccessFlags.ReadWrite);
                builder.UseRendererList(sdfList);
                builder.UseTexture(currDistHandle, AccessFlags.Write);
                builder.UseTexture(prevDistHandle, AccessFlags.Read);

                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) =>
                {
                    // Must be set per-camera inside the render func, not in RecordRenderGraph.
                    ctx.cmd.SetGlobalTexture(s_PrevSdfDistTexId, data.prevDist);

                    ctx.cmd.SetRenderTarget(
                        (RenderTargetIdentifier)data.colorTarget,
                        (RenderTargetIdentifier)data.depthTarget);
                    ctx.cmd.SetRandomWriteTarget(1, data.currRT);
                    ctx.cmd.DrawRendererList(data.rendererList);
                    ctx.cmd.ClearRandomWriteTargets();
                });
            }
        }
    }
}
