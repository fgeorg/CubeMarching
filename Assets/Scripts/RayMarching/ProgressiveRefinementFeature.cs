using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;

// Provides progressive (multiframe) refinement for SDF ray marching via UAV write.
// The SDF shader writes NDC Z to RWTexture2D _CurrSdfDepthTex (UAV slot 1).
//
// Per-camera double-buffer (no ping-pong swap):
//   Each frame:
//     1. SdfMrtPass  — sets _PrevSdfDepthTex = prevHandle, draws SDF,
//                      UAV-writes current NDC Z into currHandle.
//     2. CopyToPrevPass — copies currHandle → prevHandle so next frame
//                         can read it as _PrevSdfDepthTex.
//                         If the camera moved OR the SDF scene was rebuilt,
//                         clears prevHandle to black instead (invalidates cache).
//
// Reprojection uses UNITY_MATRIX_I_VP (current frame) in the shader, which is
// the exact inverse of TransformWorldToHClip. No per-frame matrix upload needed.
//
// Setup:
//   1. Add this feature to your URP ForwardRenderer asset.
//   2. Enable _PROGRESSIVE_REFINEMENT_ON on the SDF material.
//   3. The SDF shader must use LightMode = "SdfMrt".
[ExecuteInEditMode]
public class ProgressiveRefinementFeature : ScriptableRendererFeature
{
    // -------------------------------------------------------------------------
    // Per-camera persistent state (keyed by Camera.GetInstanceID)
    // -------------------------------------------------------------------------
    struct CameraState
    {
        public RTHandle  prevHandle;  // previous frame's depth (read by shader)
        public RTHandle  currHandle;  // current  frame's depth (UAV write target)
        public bool      initialized;
        public Matrix4x4 prevViewMatrix;  // view matrix from last frame — used to detect camera movement
        // Per-camera pass instances — shared instances are wrong because
        // AddRenderPasses runs for ALL cameras before RecordRenderGraph runs
        // for any camera, so a shared instance's Setup() gets overwritten.
        public SdfMrtPass     sdfMrtPass;
        public CopyToPrevPass copyToPrevPass;
    }

    readonly Dictionary<int, CameraState> m_CameraStates = new Dictionary<int, CameraState>();

    // Exposed for SdfDepthDebugDisplay.
    [System.NonSerialized] public RenderTexture debugPrevTex;
    [System.NonSerialized] public RenderTexture debugCurrTex;

    // Frame counter set when SdfScene.Rebuilt fires — used to invalidate caches
    // for one frame after any SDF geometry change.
    int m_SceneDirtyFrame = -1;

    // =========================================================================
    // ScriptableRendererFeature API
    // =========================================================================
    public override void Create()
    {
        // Pass instances are created lazily per-camera in AddRenderPasses.
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
        m_SceneDirtyFrame = Time.frameCount;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        Camera cam   = renderingData.cameraData.camera;
        int    camId = cam.GetInstanceID();

        if (!m_CameraStates.TryGetValue(camId, out CameraState state))
        {
            SdfMrtPass sdfMrtPass = new SdfMrtPass();
            sdfMrtPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            CopyToPrevPass copyToPrevPass = new CopyToPrevPass();
            copyToPrevPass.renderPassEvent = RenderPassEvent.AfterRenderingOpaques;
            state = new CameraState
            {
                initialized    = false,
                sdfMrtPass     = sdfMrtPass,
                copyToPrevPass = copyToPrevPass
            };
        }

        // Realloc per-camera handles if resolution changed.
        RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
        desc.msaaSamples       = 1;
        desc.depthBufferBits   = 0;
        desc.graphicsFormat    = GraphicsFormat.R32_SFloat;
        desc.enableRandomWrite = true; // required for UAV write (curr) and CopyTexture dst (prev)

        RTHandle prevHandle = state.prevHandle;
        RTHandle currHandle = state.currHandle;
        RenderingUtils.ReAllocateHandleIfNeeded(ref prevHandle, desc,
            FilterMode.Point, TextureWrapMode.Clamp, name: "_PrevSdfDepthTex");
        RenderingUtils.ReAllocateHandleIfNeeded(ref currHandle, desc,
            FilterMode.Point, TextureWrapMode.Clamp, name: "_CurrSdfDepthTex");
        state.prevHandle = prevHandle;
        state.currHandle = currHandle;

        if (!state.initialized)
        {
            state.initialized = true;
        }

        Matrix4x4 currViewMatrix = cam.worldToCameraMatrix;
        bool cameraMoved  = currViewMatrix != state.prevViewMatrix;
        bool sceneDirty   = (Time.frameCount == m_SceneDirtyFrame);
        bool invalidate   = cameraMoved || sceneDirty;
        state.prevViewMatrix = currViewMatrix;

        m_CameraStates[camId] = state;

        state.sdfMrtPass.Setup(state.currHandle, state.prevHandle);
        state.copyToPrevPass.Setup(state.currHandle, state.prevHandle, invalidate);

        // Expose for SdfDepthDebugDisplay — game camera only so the scene view
        // camera doesn't overwrite these references after the game camera sets them.
        if (cam.cameraType == CameraType.SceneView)
        {
            debugPrevTex = state.prevHandle?.rt;
            debugCurrTex = state.currHandle?.rt;
        }

        renderer.EnqueuePass(state.sdfMrtPass);
        renderer.EnqueuePass(state.copyToPrevPass);
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
    // SdfMrtPass
    // Sets _PrevSdfDepthTex global, then draws SDF with UAV depth capture.
    // Reprojection uses UNITY_MATRIX_I_VP in the shader — no matrix upload.
    // =========================================================================
    class SdfMrtPass : ScriptableRenderPass
    {
        static readonly ShaderTagId s_SdfMrtTag        = new ShaderTagId("SdfMrt");
        static readonly int         s_PrevSdfDepthTexId = Shader.PropertyToID("_PrevSdfDepthTex");

        RTHandle m_CurrHandle;
        RTHandle m_PrevHandle;

        public void Setup(RTHandle currHandle, RTHandle prevHandle)
        {
            m_CurrHandle = currHandle;
            m_PrevHandle = prevHandle;
        }

        class PassData
        {
            public RendererListHandle rendererList;
            public TextureHandle      colorTarget;
            public TextureHandle      depthTarget;
            public RenderTexture      currRT;
            public TextureHandle      prevDepth;
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

            // Import currHandle — declares UAV write for RG barrier tracking.
            TextureHandle currDepthHandle = renderGraph.ImportTexture(m_CurrHandle);

            // Import prevHandle — fallback to black on first frame.
            TextureHandle prevDepthHandle = renderGraph.defaultResources.blackTexture;
            if (m_PrevHandle != null)
            {
                TextureHandle imported = renderGraph.ImportTexture(m_PrevHandle);
                if (imported.IsValid())
                {
                    prevDepthHandle = imported;
                }
            }

            using (var builder = renderGraph.AddUnsafePass<PassData>("SdfMrtPass", out PassData passData))
            {
                passData.rendererList = sdfList;
                passData.colorTarget  = resourceData.activeColorTexture;
                passData.depthTarget  = resourceData.cameraDepth;
                passData.currRT       = m_CurrHandle.rt;
                passData.prevDepth    = prevDepthHandle;

                builder.UseTexture(resourceData.activeColorTexture, AccessFlags.ReadWrite);
                builder.UseTexture(resourceData.cameraDepth, AccessFlags.ReadWrite);
                builder.UseRendererList(sdfList);
                if (currDepthHandle.IsValid())
                {
                    builder.UseTexture(currDepthHandle, AccessFlags.Write);
                }
                if (prevDepthHandle.IsValid())
                {
                    builder.UseTexture(prevDepthHandle, AccessFlags.Read);
                }

                builder.AllowGlobalStateModification(true);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) =>
                {
                    // Set _PrevSdfDepthTex per-camera immediately before this camera's draw.
                    ctx.cmd.SetGlobalTexture(s_PrevSdfDepthTexId, data.prevDepth);

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

    // =========================================================================
    // CopyToPrevPass
    // After SdfMrtPass, copies currHandle → prevHandle so next frame's
    // SdfMrtPass reads the correct previous-frame depth.
    // If invalidate is true (camera moved or SDF scene rebuilt), clears
    // prevHandle to black instead so stale depth is not reused.
    // =========================================================================
    class CopyToPrevPass : ScriptableRenderPass
    {
        RTHandle m_CurrHandle;
        RTHandle m_PrevHandle;
        bool     m_Invalidate;

        public void Setup(RTHandle currHandle, RTHandle prevHandle, bool invalidate)
        {
            m_CurrHandle  = currHandle;
            m_PrevHandle  = prevHandle;
            m_Invalidate  = invalidate;
        }

        class PassData
        {
            public TextureHandle currTex;
            public TextureHandle prevTex;
            public RenderTexture currRT;
            public RenderTexture prevRT;
            public bool          invalidate;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            if (m_CurrHandle == null || m_CurrHandle.rt == null ||
                m_PrevHandle == null || m_PrevHandle.rt == null)
            {
                return;
            }

            TextureHandle currTex = renderGraph.ImportTexture(m_CurrHandle);
            TextureHandle prevTex = renderGraph.ImportTexture(m_PrevHandle);

            if (!currTex.IsValid() || !prevTex.IsValid())
            {
                return;
            }

            using (var builder = renderGraph.AddUnsafePass<PassData>("CopyToPrevPass", out PassData passData))
            {
                passData.currTex    = currTex;
                passData.prevTex    = prevTex;
                passData.currRT     = m_CurrHandle.rt;
                passData.prevRT     = m_PrevHandle.rt;
                passData.invalidate = m_Invalidate;

                builder.UseTexture(currTex, AccessFlags.Read);
                builder.UseTexture(prevTex, AccessFlags.Write);

                builder.SetRenderFunc((PassData data, UnsafeGraphContext ctx) =>
                {
                    if (data.invalidate)
                    {
                        ctx.cmd.SetRenderTarget(data.prevRT);
                        ctx.cmd.ClearRenderTarget(false, true, Color.black);
                    }
                    else
                    {
                        ctx.cmd.CopyTexture(data.currRT, data.prevRT);
                    }
                });
            }
        }
    }
}
