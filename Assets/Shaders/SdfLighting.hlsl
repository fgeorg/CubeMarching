#ifndef SDF_LIGHTING_INCLUDED
#define SDF_LIGHTING_INCLUDED

// Shared PBR lighting for SDF/ray-march shaders (no shadow map — SDF geometry
// is invisible to the shadow caster pass so shadow coords are zeroed out).
// Must be included after Lighting.hlsl and SdfSceneDistanceGpu.hlsl (for SdfMaterial).
half4 SdfLighting(float3 positionWS, half3 normalWS, float4 positionCS, SdfMaterial mat, half4 tint) {
    SurfaceData surfaceData = (SurfaceData)0;
    surfaceData.albedo     = half3(tint.rgb * mat.color.rgb);
    surfaceData.metallic   = (half)mat.metallic;
    surfaceData.smoothness = (half)mat.smoothness;
    surfaceData.occlusion  = 1.0h;
    surfaceData.alpha      = 1.0h;
    surfaceData.normalTS   = half3(0, 0, 1);

    InputData inputData = (InputData)0;
    inputData.positionWS              = positionWS;
    inputData.normalWS                = normalWS;
    inputData.viewDirectionWS         = SafeNormalize(GetCameraPositionWS() - positionWS);
    inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(positionCS);
    // Ray-march shaders have no vertex stage so GI is sampled fully per-pixel.
    // APV (Adaptive Probe Volumes, Unity 6 default) needs the world position.
    #if defined(PROBE_VOLUMES_L1) || defined(PROBE_VOLUMES_L2)
        inputData.bakedGI = SAMPLE_GI(half3(0, 0, 0), GetAbsolutePositionWS(positionWS), normalWS, unity_ProbeVolumeMin.xyz, unity_ProbeVolumeSizeInv.xyz);
    #else
        inputData.bakedGI = SampleSH(normalWS);
    #endif
    inputData.shadowMask              = unity_ProbesOcclusion;
    inputData.shadowCoord             = float4(0, 0, 0, 0);
    half3 rgb = UniversalFragmentPBR(inputData, surfaceData).rgb;
    return half4(rgb, tint.a * mat.color.a);
}

#endif
