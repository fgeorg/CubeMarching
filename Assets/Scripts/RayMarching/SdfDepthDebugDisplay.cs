using UnityEngine;

// Displays the progressive refinement depth textures (prev/curr) on two quads.
// Setup:
//   1. Assign the ProgressiveRefinementFeature asset from your URP renderer.
//   2. Assign a Renderer for prevQuad (shows last frame's depth — _PrevSdfDepthTex).
//   3. Assign a Renderer for currQuad (shows this frame's depth capture — currHandle).
//   Materials are created automatically — no manual render texture or material needed.
[ExecuteInEditMode]
public class SdfDepthDebugDisplay : MonoBehaviour
{
    public ProgressiveRefinementFeature feature;
    public Renderer prevQuad;
    public Renderer currQuad;

    Material m_PrevMat;
    Material m_CurrMat;

    static readonly int s_MainTex = Shader.PropertyToID("_MainTex");

    void OnEnable()
    {
        EnsureMaterials();
    }

    void OnDisable()
    {
        if (m_PrevMat != null) DestroyImmediate(m_PrevMat);
        if (m_CurrMat != null) DestroyImmediate(m_CurrMat);
        m_PrevMat = null;
        m_CurrMat = null;
    }

    void EnsureMaterials()
    {
        Shader shader = Shader.Find("Debug/DepthTex");
        if (shader == null)
        {
            return;
        }

        if (prevQuad != null && m_PrevMat == null)
        {
            m_PrevMat = new Material(shader);
            m_PrevMat.name = "DebugDepth_Prev";
            prevQuad.sharedMaterial = m_PrevMat;
        }

        if (currQuad != null && m_CurrMat == null)
        {
            m_CurrMat = new Material(shader);
            m_CurrMat.name = "DebugDepth_Curr";
            currQuad.sharedMaterial = m_CurrMat;
        }
    }

    void Update()
    {
        if (feature == null)
        {
            return;
        }

        EnsureMaterials();

        if (m_PrevMat != null && feature.debugPrevTex != null)
        {
            m_PrevMat.SetTexture(s_MainTex, feature.debugPrevTex);
        }

        if (m_CurrMat != null && feature.debugCurrTex != null)
        {
            m_CurrMat.SetTexture(s_MainTex, feature.debugCurrTex);
        }
    }
}
