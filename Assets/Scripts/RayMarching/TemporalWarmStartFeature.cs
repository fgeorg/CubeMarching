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
// CopySdfDepth shader.  Then enable _TEMPORAL_WARMSTART_ON on the SDF material.
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

    // -------------------------------------------------------------------------
    // Persistent state (survives between frames)
    // -------------------------------------------------------------------------
    RTHandle m_PrevHandle;          // previous frame's captured depth
    RTHandle m_CurrHandle;          // current frame will write here
    bool m_Initialized;

    Matrix4x4 m_PrevInvVP = Matrix4x4.identity;
    Vector3 m_PrevCameraPos = Vector3.zero;

    Material m_CopyDepthMat;

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

        // Lazily create the blit material.
        if (m_CopyDepthMat == null)
        {
            m_CopyDepthMat = CoreUtils.CreateEngineMaterial(copySdfDepthShader);
        }

        // Skip non-game cameras (scene view, preview).
        if (renderingData.cameraData.camera.cameraType != CameraType.Game)
        {
            return;
        }

        // Build a descriptor for a single-channel float screen-sized texture.
        RenderTextureDescriptor desc = renderingData.cameraData.cameraTargetDescriptor;
        desc.msaaSamples = 1;
        desc.depthBufferBits = 0;
        desc.graphicsFormat = GraphicsFormat.R32_SFloat;

        RenderingUtils.ReAllocateHandleIfNeeded(ref m_PrevHandle, desc,
            FilterMode.Point, TextureWrapMode.Clamp, name: "_PrevSdfDepthTex");
        RenderingUtils.ReAllocateHandleIfNeeded(ref m_CurrHandle, desc,
            FilterMode.Point, TextureWrapMode.Clamp, name: "_CurrSdfDepthTex");

        // Ping-pong: after the first frame, swap so prev = last frame's capture.
        if (m_Initialized)
        {
            RTHandle tmp = m_PrevHandle;
            m_PrevHandle = m_CurrHandle;
            m_CurrHandle = tmp;
        }
        else
        {
            m_Initialized = true;
        }

        m_BindPass.Setup(m_PrevHandle, m_PrevInvVP, m_PrevCameraPos);
        m_CapturePass.Setup(m_CurrHandle, m_CopyDepthMat);

        // Record the current camera matrices for use NEXT frame.
        Camera cam = renderingData.cameraData.camera;
        Matrix4x4 gpuProj = GL.GetGPUProjectionMatrix(cam.projectionMatrix, renderIntoTexture: true);
        Matrix4x4 vp = gpuProj * cam.worldToCameraMatrix;
        m_PrevInvVP = vp.inverse;
        m_PrevCameraPos = cam.transform.position;

        renderer.EnqueuePass(m_BindPass);
        renderer.EnqueuePass(m_CapturePass);
    }

    protected override void Dispose(bool disposing)
    {
        m_PrevHandle?.Release();
        m_CurrHandle?.Release();
        CoreUtils.Destroy(m_CopyDepthMat);
    }

    // =========================================================================
    // BindTemporalDataPass
    // Sets the previous-frame depth texture and camera matrices as global shader
    // properties before the transparent (SDF) pass executes.
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
            TextureHandle prevDepthHandle = renderGraph.ImportTexture(m_PrevDepthHandle);
            if (!prevDepthHandle.IsValid())
            {
                return;
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
    // m_CurrHandle (RFloat) so it can be read as a warm-start next frame.
    // =========================================================================
    class CaptureDepthPass : ScriptableRenderPass
    {
        RTHandle m_CurrHandle;
        Material m_CopyDepthMat;
        static readonly Vector4 k_ScaleBias = new Vector4(1f, 1f, 0f, 0f);

        public void Setup(RTHandle currHandle, Material copyDepthMat)
        {
            m_CurrHandle    = currHandle;
            m_CopyDepthMat  = copyDepthMat;
        }

        class PassData
        {
            public TextureHandle src;
            public TextureHandle dst;
            public Material mat;
        }

        public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
        {
            UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();

            if (resourceData.isActiveTargetBackBuffer)
            {
                return;
            }

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
