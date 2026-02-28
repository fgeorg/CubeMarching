#ifndef SDF_SCENE_DISTANCE_GPU_HLSL
#define SDF_SCENE_DISTANCE_GPU_HLSL

#include "SdfNodeType.hlsl"
#include "SdfStack.hlsl"

struct SdfNode {
    // x=type, y=param0, z=param1, w=param2
    float4 typeAndParams;
    int primitiveIndex; // index into _SdfPrimitives; -1 for ops
};
StructuredBuffer<SdfNode> _SdfNodes;

struct SdfPrimitive {
    float4x4 transform; // worldToLocal
    float4 albedo;
};
StructuredBuffer<SdfPrimitive> _SdfPrimitives;

// Primitive SDFs — https://iquilezles.org/articles/distfunctions/
float SdfSphere(float3 p, float r) {
    return length(p) - r;
}

float SdfBox(float3 p, float3 halfExtents) {
    float3 q = abs(p) - halfExtents;
    return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
}

float SdfTorus(float3 p, float ringRadius, float tubeRadius) {
    float2 q = float2(length(p.xy) - ringRadius, p.z);
    return length(q) - tubeRadius;
}

// Smooth boolean ops — https://iquilezles.org/articles/smin/
float SmoothUnion(float a, float b, float k) {
    float h = max(k - abs(a - b), 0.0);
    return min(a, b) - h * h * 0.25 / k;
}

float SmoothSubtract(float a, float b, float k) {
    return -SmoothUnion(-a,  b, k);
}
float SmoothIntersect(float a, float b, float k) {
    return -SmoothUnion(-a, -b, k);
}

// Postfix stack evaluator. Primitives push; binary ops pop two and push result;
// unary ops modify top in place.
// Requires _SdfNodeCount to be declared before this file is included
// (it lives in the UnityPerMaterial CBUFFER in the including shader).
float GetDistanceToScene(float3 p) {
    SdfStack stack = (SdfStack)0;
    int sp = 0;
    [loop]
    for (int i = 0; i < _SdfNodeCount; i++) {
        SdfNode node = _SdfNodes[i];
        int t = (int)node.typeAndParams.x;
        float k = node.typeAndParams.y;
        if (t < SDF_PRIMITIVES_END) { // primitive — push
            // convert to local space before evaluating the distance function
            float3 lp = mul(_SdfPrimitives[node.primitiveIndex].transform, float4(p, 1.0)).xyz;
            float d;
            if (t == SDF_SPHERE) {
                d = SdfSphere(lp, node.typeAndParams.y);
            }
            else if (t == SDF_BOX) {
                d = SdfBox(lp, node.typeAndParams.yzw);
            }
            else { // SDF_TORUS
                d = SdfTorus(lp, node.typeAndParams.y, node.typeAndParams.z);
            }
            if (sp < STACK_SIZE) {
                SetStackValue(stack, sp, d);
                sp++;
            }
        }
        else if (t <= SDF_BINARY_OPS_END && sp >= 2) { // binary operator — pop two, push result
            float b = GetStackValue(stack, --sp);
            float a = GetStackValue(stack, --sp);
            float r;
            if (t == SDF_UNION) {
                r = min(a, b);
            }
            else if (t == SDF_SMOOTH_UNION) {
                r = SmoothUnion(a, b, k);
            }
            else if (t == SDF_INTERSECT) {
                r = max(a, b);
            }
            else if (t == SDF_SMOOTH_INTERSECT) {
                r = SmoothIntersect(a, b, k);
            }
            else if (t == SDF_SUBTRACT) {
                r = max(a, -b);
            }
            else { // SDF_SMOOTH_SUBTRACT
                r = SmoothSubtract(a, b, k);
            }
            SetStackValue(stack, sp++, r);
        }
        else if (t <= SDF_UNARY_OPS_END && sp >= 1) { // unary modifier — modify top in place
            float top = GetStackValue(stack, sp - 1);
            if (t == SDF_SHELL) {
                SetStackValue(stack, sp - 1, abs(top) - k);
            }
            else { // SDF_EXPAND
                SetStackValue(stack, sp - 1, top - k);
            }
        }
    }
    return sp > 0 ? GetStackValue(stack, 0) : 1e10;
}

// Returns albedo by evaluating the full postfix operator tree with operator-aware blending.
// — Union/Smooth Union:         blend toward whichever side is closer
// — Intersect/Smooth Intersect: blend toward whichever side is more constraining
// — Subtract/Smooth Subtract:   base (a) material, blending to cutter (b) at the cut face
// — Shell, Expand:              material passes through unchanged
float4 GetMaterialAtScene(float3 p) {
    SdfStack stack = (SdfStack)0;
    SdfMaterialStack matStack = (SdfMaterialStack)0;
    int sp = 0;
    [loop]
    for (int i = 0; i < _SdfNodeCount; i++) {
        SdfNode node = _SdfNodes[i];
        int t = (int)node.typeAndParams.x;
        float k = node.typeAndParams.y;
        if (t < SDF_PRIMITIVES_END) { // primitive — push
            float3 lp = mul(_SdfPrimitives[node.primitiveIndex].transform, float4(p, 1.0)).xyz;
            float d;
            if (t == SDF_SPHERE) {
                d = SdfSphere(lp, node.typeAndParams.y);
            }
            else if (t == SDF_BOX) {
                d = SdfBox(lp, node.typeAndParams.yzw);
            }
            else { // SDF_TORUS
                d = SdfTorus(lp, node.typeAndParams.y, node.typeAndParams.z);
            }
            if (sp < STACK_SIZE) {
                SetStackValue(stack, sp, d);
                SetMaterialStackValue(matStack, sp, _SdfPrimitives[node.primitiveIndex].albedo);
                sp++;
            }
        }
        else if (t <= SDF_BINARY_OPS_END && sp >= 2) { // binary operator — pop two, push result
            float b_d = GetStackValue(stack, --sp);
            float4 b_mat = GetMaterialStackValue(matStack, sp);
            float a_d = GetStackValue(stack, --sp);
            float4 a_mat = GetMaterialStackValue(matStack, sp);
            float r_d;
            float4 r_mat;
            if (t == SDF_UNION) {
                r_d = min(a_d, b_d);
                r_mat = a_d < b_d ? a_mat : b_mat;
            }
            else if (t == SDF_SMOOTH_UNION) {
                r_d = SmoothUnion(a_d, b_d, k);
                float h = smoothstep(0.0, 1.0, 0.5 + 0.5 * (b_d - a_d) / k);
                r_mat = lerp(b_mat, a_mat, h);
            }
            else if (t == SDF_INTERSECT) {
                r_d = max(a_d, b_d);
                r_mat = a_d > b_d ? a_mat : b_mat;
            }
            else if (t == SDF_SMOOTH_INTERSECT) {
                r_d = SmoothIntersect(a_d, b_d, k);
                float h = smoothstep(0.0, 1.0, 0.5 + 0.5 * (a_d - b_d) / k);
                r_mat = lerp(b_mat, a_mat, h);
            }
            else if (t == SDF_SUBTRACT) {
                r_d = max(a_d, -b_d);
                r_mat = a_d >= -b_d ? a_mat : b_mat;
            }
            else { // SDF_SMOOTH_SUBTRACT
                r_d = SmoothSubtract(a_d, b_d, k);
                float h = smoothstep(0.0, 1.0, 0.5 + 0.5 * (a_d + b_d) / k);
                r_mat = lerp(b_mat, a_mat, h);
            }
            SetStackValue(stack, sp, r_d);
            SetMaterialStackValue(matStack, sp, r_mat);
            sp++;
        }
        else if (t <= SDF_UNARY_OPS_END && sp >= 1) { // unary modifier — modify top in place
            float top = GetStackValue(stack, sp - 1);
            if (t == SDF_SHELL) {
                SetStackValue(stack, sp - 1, abs(top) - k);
            }
            else { // SDF_EXPAND
                SetStackValue(stack, sp - 1, top - k);
            }
            // material passes through unchanged
        }
    }
    return sp > 0 ? GetMaterialStackValue(matStack, 0) : float4(1, 1, 1, 1);
}

#endif
