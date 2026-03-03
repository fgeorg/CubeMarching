using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

// Captures the hardware depth buffer after the SDF transparent pass and feeds it
// back the following frame as a warm-start hint for RayMarch().
//
// Setup: add this feature to your URP ForwardRenderer asset, assign the
// CopySdfDepth shader. Then enable _TEMPORAL_WARMSTART_ON on the SDF material.
[ExecuteInEditMode]
public class TemporalWarmStartFeature : ScriptableRendererFeature
{
    // -------------------------------------------------------------------------
    // Settings exposed in the Inspector
    // -------------------------------------------------------------------------
    [Tooltip("Assign the Hidden/CopySdfDepth shader here.")]
    public Shader copySdfDepthShader;

    // -------------------------------------------------------------------------
    // Passes
    // -------------------------------------------------------------------------
    BindTemporalDataPass m_BindPass;
    CaptureDepthPass m_CapturePass;
    Material m_CopyDepthMat;

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
    }
    readonly Dictionary<int, CameraState> m_CameraStates = new Dictionary<int, CameraState>();

    // =========================================================================
    // ScriptableRendererFeature API
    // =========================================================================
    public override void Create()
    {
        m_BindPass = new BindTemporalDataPass();
        m_BindPass.renderPassEvent = RenderPassEvent.BeforeRenderingTransparents;

        m_CapturePass = new CaptureDepthPass();
        m_CapturePass.renderPassEvent = RenderPassEvent.AfterRenderingTransparents;
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (copySdfDepthShader == null)
        {
            return;
        }

        if (m_CopyDepthMat == null)
        {
            m_CopyDepthMat = CoreUtils.CreateEngineMaterial(copySdfDepthShader);
        }

        Camera cam = renderingData.cameraData.camera;
        int camId = cam.GetInstanceID();

        if (!m_CameraStates.TryGetValue(camId, out CameraState state))
        {
            state = new CameraState
            {
                prevInvVP = Matrix4x4.identity,
                prevCameraPos = Vector3.zero,
                initialized = false
            };
        }

        // Realloc per-camera handles if the resolution changed.
        RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
        desc.msaaSamples = 1;
        desc.depthBufferBits = 0;
        desc.graphicsFormat = GraphicsFormat.R32_SFloat;

        RTHandle prevHandle = state.prevHandle;
        RTHandle currHandle = state.currHandle;
        RenderingUtils.ReAllocateHandleIfNeeded(ref prevHandle, desc,
            FilterMode.Point, TextureWrapMode.Clamp, name: "_PrevSdfDepthTex");
        RenderingUtils.ReAllocateHandleIfNeeded(ref currHandle, desc,
            FilterMode.Point, TextureWrapMode.Clamp, name: "_CurrSdfDepthTex");
        state.prevHandle = prevHandle;
        state.currHandle = currHandle;

        // Ping-pong per camera: after first frame, prev = last frame's capture.
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

        m_BindPass.Setup(state.prevHandle, state.prevInvVP, state.prevCameraPos);
        m_CapturePass.Setup(state.currHandle, m_CopyDepthMat);

        // Record current frame's matrices for next frame.
        Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, renderIntoTexture: true);
        state.prevInvVP = (gpuProj * cam.worldToCameraMatrix).inverse;
        state.prevCameraPos = cam.transform.position;

        m_CameraStates[camId] = state;

        renderer.EnqueuePass(m_BindPass);
        renderer.EnqueuePass(m_CapturePass);
    }

    protected override void Dispose(bool disposing)
    {
        foreach (CameraState state in m_CameraStates.Values)
        {
            state.prevHandle?.Release();
            state.currHandle?.Release();
        }
        m_CameraStates.Clear();
        CoreUtils.Destroy(m_CopyDepthMat);
    }

    // =========================================================================
    // BindTemporalDataPass
    // Sets the previous-frame depth texture and camera matrices as global shader
    // properties before the transparent (SDF) pass executes.
    // Always sets the global — never early-returns — so game-camera globals
    // never leak into the scene-view camera's draw.
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
            // Fall back to black (prevNdcZ == 0 disables warm-start in the shader)
            // on the first frame before any depth has been captured.
            TextureHandle prevDepthHandle = renderGraph.defaultResources.blackTexture;

            if (m_PrevDepthHandle != null)
            {
                TextureHandle imported = renderGraph.ImportTexture(m_PrevDepthHandle);
                if (imported.IsValid())
                {
                    prevDepthHandle = imported;
                }
            }

            using (var builder = renderGraph.AddRasterRenderPass<PassData>("BindTemporalData", out var passData))
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
    // CaptureDepthPass
    // After transparents have rendered, copies the hardware depth buffer into
    // the per-camera currHandle (R32_SFloat) for use as warm-start next frame.
    // =========================================================================
    class CaptureDepthPass : ScriptableRenderPass
    {
        RTHandle m_CurrHandle;
        Material m_CopyDepthMat;

        public void Setup(RTHandle currHandle, Material copyDepthMat)
        {
            m_CurrHandle   = currHandle;
            m_CopyDepthMat = copyDepthMat;
        }

        class PassData { }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
            TextureHandle src = resourceData.cameraDepth;
            TextureHandle dst = renderGraph.ImportTexture(m_CurrHandle);

            if (!src.IsValid() || !dst.IsValid())
            {
                return;
            }

            RenderGraphUtils.BlitMaterialParameters blit =
                new RenderGraphUtils.BlitMaterialParameters(src, dst, m_CopyDepthMat, 0);
            renderGraph.AddBlitPass(blit, "CaptureSdfDepth");
        }
    }
}
