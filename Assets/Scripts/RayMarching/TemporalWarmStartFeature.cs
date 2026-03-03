using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// Provides temporal warm-start for SDF ray marching via MRT (Multiple Render Targets).
// The SDF shader writes NDC Z to SV_Target1 (currHandle) alongside its colour output,
// so depth capture is free — no blit, no extra shader, no extra draw call.
//
// Per-camera ping-pong:
//   Frame N:  bind prevHandle as _PrevSdfDepthTex → draw SDF (MRT) → currHandle captured
//   Frame N+1: prevHandle ↔ currHandle swap → bind prevHandle → draw SDF → ...
//
// Setup:
//   1. Add this feature to your URP ForwardRenderer asset.
//   2. Enable _TEMPORAL_WARMSTART_ON on the SDF material.
//   3. The SDF shader must use LightMode = "SdfMrt" (feature owns the draw call).
[ExecuteInEditMode]
public class TemporalWarmStartFeature : ScriptableRendererFeature
{
    // -------------------------------------------------------------------------
    // Per-camera persistent state (keyed by Camera.GetInstanceID)
    // Avoids cross-camera contamination between game view and scene view.
    // -------------------------------------------------------------------------
    struct CameraState
    {
        public Matrix4x4 prevInvVP;
        public Vector3 prevCameraPos;
        public RTHandle prevHandle;
        public RTHandle currHandle;
        public bool initialized;
        // Per-camera pass instances — shared instances are wrong because
        // AddRenderPasses runs for ALL cameras before RecordRenderGraph runs
        // for any camera, so a shared instance's Setup() gets overwritten.
        public BindTemporalDataPass bindPass;
        public SdfMrtPass sdfMrtPass;
    }

    readonly Dictionary<int, CameraState> m_CameraStates = new Dictionary<int, CameraState>();

    // Exposed for the SdfDepthDebugDisplay helper MonoBehaviour.
    // Points to the previous frame's captured depth texture for the main camera.
    [System.NonSerialized] public RenderTexture debugPrevTex;
    [System.NonSerialized] public RenderTexture debugCurrTex;

    // =========================================================================
    // ScriptableRendererFeature API
    // =========================================================================
    public override void Create()
    {
        // Pass instances are created lazily per-camera in AddRenderPasses.
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        Camera cam = renderingData.cameraData.camera;
        int camId = cam.GetInstanceID();

        if (!m_CameraStates.TryGetValue(camId, out CameraState state))
        {
            BindTemporalDataPass bindPass = new BindTemporalDataPass();
            bindPass.renderPassEvent = RenderPassEvent.BeforeRenderingOpaques;
            SdfMrtPass sdfMrtPass = new SdfMrtPass();
            sdfMrtPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            state = new CameraState
            {
                prevInvVP = Matrix4x4.identity,
                prevCameraPos = Vector3.zero,
                initialized = false,
                bindPass = bindPass,
                sdfMrtPass = sdfMrtPass
            };
        }

        // Realloc per-camera handles if resolution changed.
        RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
        desc.msaaSamples = 1;
        desc.depthBufferBits = 0;
        desc.graphicsFormat = GraphicsFormat.R32_SFloat;
        desc.enableRandomWrite = true; // required for SetRandomWriteTarget UAV write

        RTHandle prevHandle = state.prevHandle;
        RTHandle currHandle = state.currHandle;
        RenderingUtils.ReAllocateHandleIfNeeded(ref prevHandle, desc,
            FilterMode.Point, TextureWrapMode.Clamp, name: "_PrevSdfDepthTex");
        RenderingUtils.ReAllocateHandleIfNeeded(ref currHandle, desc,
            FilterMode.Point, TextureWrapMode.Clamp, name: "_CurrSdfDepthTex");
        state.prevHandle = prevHandle;
        state.currHandle = currHandle;

        // Ping-pong: after first frame, prev = last frame's MRT capture.
        if (state.initialized)
        {
            RTHandle tmp = state.prevHandle;
            state.prevHandle = state.currHandle;
            state.currHandle = tmp;
        }
        else
        {
            state.initialized = true;
        }

        state.bindPass.Setup(state.prevHandle, state.prevInvVP, state.prevCameraPos);
        state.sdfMrtPass.Setup(state.currHandle);

        // Record current frame's matrices for next frame.
        Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, renderIntoTexture: true);
        state.prevInvVP = (gpuProj * cam.worldToCameraMatrix).inverse;
        state.prevCameraPos = cam.transform.position;

        m_CameraStates[camId] = state;

        // Expose for SdfDepthDebugDisplay — only update for game cameras so the
        // scene view camera doesn't overwrite these after the game camera runs.
        if (cam.cameraType == CameraType.Game)
        {
            debugPrevTex = state.prevHandle?.rt;
            debugCurrTex = state.currHandle?.rt;
        }

        renderer.EnqueuePass(state.bindPass);
        renderer.EnqueuePass(state.sdfMrtPass);
    }

    protected override void Dispose(bool disposing)
    {
        foreach (CameraState state in m_CameraStates.Values)
        {
            state.prevHandle?.Release();
            state.currHandle?.Release();
        }
        m_CameraStates.Clear();
    }

    // =========================================================================
    // BindTemporalDataPass
    // Sets _PrevSdfDepthTex, _PrevInvVP, _PrevCameraPos as global shader
    // properties before the SDF MRT pass executes.
    // Falls back to black (prevNdcZ == 0 → warm-start skipped) on first frame.
    // =========================================================================
    class BindTemporalDataPass : ScriptableRenderPass
    {
        RTHandle m_PrevDepthHandle;
        Matrix4x4 m_PrevInvVP;
        Vector4 m_PrevCameraPos;

        static readonly int s_PrevSdfDepthTexId = Shader.PropertyToID("_PrevSdfDepthTex");
        static readonly int s_PrevInvVPId       = Shader.PropertyToID("_PrevInvVP");
        static readonly int s_PrevCameraPosId   = Shader.PropertyToID("_PrevCameraPos");

        public void Setup(RTHandle prevDepth, Matrix4x4 prevInvVP, Vector3 prevCameraPos)
        {
            m_PrevDepthHandle = prevDepth;
            m_PrevInvVP       = prevInvVP;
            m_PrevCameraPos   = new Vector4(prevCameraPos.x, prevCameraPos.y, prevCameraPos.z, 0f);
        }

        class PassData
        {
            public TextureHandle prevDepth;
            public Matrix4x4 prevInvVP;
            public Vector4 prevCameraPos;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            // Fall back to black (prevNdcZ == 0 disables warm-start) on first frame.
            TextureHandle prevDepthHandle = renderGraph.defaultResources.blackTexture;

            if (m_PrevDepthHandle != null)
            {
                TextureHandle imported = renderGraph.ImportTexture(m_PrevDepthHandle);
                if (imported.IsValid())
                {
                    prevDepthHandle = imported;
                }
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("BindTemporalData", out PassData passData))
            {
                passData.prevDepth     = prevDepthHandle;
                passData.prevInvVP     = m_PrevInvVP;
                passData.prevCameraPos = m_PrevCameraPos;

                builder.UseTexture(prevDepthHandle, AccessFlags.Read);
                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, RasterGraphContext ctx) =>
                {
                    ctx.cmd.SetGlobalTexture(s_PrevSdfDepthTexId, data.prevDepth);
                    ctx.cmd.SetGlobalMatrix(s_PrevInvVPId, data.prevInvVP);
                    ctx.cmd.SetGlobalVector(s_PrevCameraPosId, data.prevCameraPos);
                });
            }
        }
    }

    // =========================================================================
    // SdfMrtPass
    // Draws all renderers using LightMode="SdfMrt" via an UnsafePass.
    // Depth capture uses SetRandomWriteTarget (UAV write) instead of MRT:
    //   - render target = camera colour + depth (single colour attachment, no MRT)
    //   - UAV slot 1    = currHandle  (RWTexture2D in shader writes NDC Z here)
    // This avoids Metal native-render-pass issues where non-zero MRT slots
    // with imported external RTHandles are silently mis-routed.
    // =========================================================================
    class SdfMrtPass : ScriptableRenderPass
    {
        static readonly ShaderTagId s_SdfMrtTag = new ShaderTagId("SdfMrt");

        RTHandle m_CurrHandle;

        public void Setup(RTHandle currHandle)
        {
            m_CurrHandle = currHandle;
        }

        class PassData
        {
            public RendererListHandle rendererList;
            public TextureHandle colorTarget;
            public TextureHandle depthTarget;
            public RenderTexture currRT;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_CurrHandle == null || m_CurrHandle.rt == null)
            {
                return;
            }

            var resourceData  = frameData.Get<UniversalResourceData>();
            var renderingData = frameData.Get<UniversalRenderingData>();
            var cameraData    = frameData.Get<UniversalCameraData>();

            if (!resourceData.activeColorTexture.IsValid() || !resourceData.cameraDepth.IsValid())
            {
                return;
            }

            // Build renderer list: all transparent renderers with the SdfMrt light mode.
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

            // Import currHandle so the render graph knows this pass writes to it.
            // This causes the RG to insert a Metal texture-cache barrier so the
            // next frame's BindTemporalDataPass reads coherent UAV-written data.
            TextureHandle currDepthHandle = renderGraph.ImportTexture(m_CurrHandle);

            using (var builder = renderGraph.AddUnsafePass<PassData>("SdfMrtPass", out PassData passData))
            {
                passData.rendererList = sdfList;
                passData.colorTarget  = resourceData.activeColorTexture;
                passData.depthTarget  = resourceData.cameraDepth;
                passData.currRT       = m_CurrHandle.rt;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(resourceData.cameraDepth, AccessFlags.ReadWrite);
                builder.UseRendererList(sdfList);
                if (currDepthHandle.IsValid())
                {
                    builder.UseTexture(currDepthHandle, AccessFlags.Write);
                }

                builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) =>
                {
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
