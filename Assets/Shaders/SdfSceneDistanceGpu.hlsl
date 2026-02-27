#ifndef SDF_SCENE_DISTANCE_GPU_HLSL
#define SDF_SCENE_DISTANCE_GPU_HLSL

#include "SdfNodeTypes.hlsl"
#include "SdfStack.hlsl"

struct SdfNode
{
    float4 typeAndParams; // x=type, y=param0, z=param1, w=param2
    float4x4 transform;
};
StructuredBuffer<SdfNode> _SdfNodes;

// Smooth boolean ops — https://iquilezles.org/articles/smin/
float SmoothUnion(float a, float b, float k)
{
    float h = max(k - abs(a - b), 0.0);
    return min(a, b) - h * h * 0.25 / k;
}

float SmoothSubtract(float a, float b, float k) { return -SmoothUnion(-a,  b, k); }
float SmoothIntersect(float a, float b, float k) { return -SmoothUnion(-a, -b, k); }

// Postfix stack evaluator. Primitives push; binary ops pop two and push result;
// unary ops modify top in place.
// Requires _SdfNodeCount to be declared before this file is included
// (it lives in the UnityPerMaterial CBUFFER in the including shader).
float GetDistanceToScene(float3 p)
{
    SdfStack stack = (SdfStack)0;
    int sp = 0;
    [loop]
    for (int i = 0; i < _SdfNodeCount; i++)
    {
        SdfNode node = _SdfNodes[i];
        int t = (int)node.typeAndParams.x;
        float k = node.typeAndParams.y;
        if (t < SDF_PRIMITIVES_END) // primitive — push
        {
            float3 lp = mul(node.transform, float4(p, 1.0)).xyz;
            float d;
            if (t == SDF_SPHERE)
            d = length(lp) - node.typeAndParams.y;
            else if (t == SDF_BOX)
            {
                float3 bh = node.typeAndParams.yzw;
                float3 q = abs(lp) - bh;
                d = length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
            }
            else // SDF_TORUS
            {
                float2 q2 = float2(length(lp.xy) - node.typeAndParams.y, lp.z);
                d = length(q2) - node.typeAndParams.z;
            }
            if (sp < STACK_SIZE)
            {
                SetStackValue(stack, sp, d);
                sp++;
            }
        }
        else if (t <= SDF_UNARY_OPS_END && sp >= 2) // binary operator — pop two, push result
        {
            sp--;
            float b = GetStackValue(stack, sp);
            sp--;
            float a = GetStackValue(stack, sp);
            float r;
            if      (t == SDF_UNION)            r = min(a, b);
            else if (t == SDF_SMOOTH_UNION)     r = SmoothUnion(a, b, k);
            else if (t == SDF_INTERSECT)        r = max(a, b);
            else if (t == SDF_SMOOTH_INTERSECT) r = SmoothIntersect(a, b, k);
            else if (t == SDF_SUBTRACT)         r = max(a, -b);
            else                                r = SmoothSubtract(a, b, k); // SDF_SMOOTH_SUBTRACT
            SetStackValue(stack, sp, r);
            sp++;
        }
        else if (t <= SDF_UNARY_OPS_END && sp >= 1) // unary modifier — modify top in place
        {
            float top = GetStackValue(stack, sp - 1);
            if (t == SDF_SHELL) SetStackValue(stack, sp - 1, abs(top) - k);
            else                SetStackValue(stack, sp - 1, top - k); // SDF_EXPAND
        }
    }
    return sp > 0 ? GetStackValue(stack, 0) : 1e10;
}

#endif
