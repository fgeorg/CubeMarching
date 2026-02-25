Shader "Custom/Flat Wireframe" {
	Properties {
		_Color ("Tint", Color) = (1, 1, 1, 1)
		_MainTex ("Albedo", 2D) = "white" {}
		_Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5
		_WireframeColor ("Wireframe Color", Color) = (0, 0, 0, 1)
		_WireframeSmoothing ("Wireframe Smoothing", Range(0, 10)) = 1
		_WireframeThickness ("Wireframe Thickness", Range(0, 10)) = 1
		[Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2
		[Enum(Off,0,On,1)] _ZWrite ("ZWrite", Float) = 1
		[Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 4
	}

	SubShader {
		Tags {
			"RenderType" = "Opaque"
			"RenderPipeline" = "UniversalPipeline"
			"Queue" = "Geometry"
		}

		Pass {
			Name "ForwardLit"
			Tags { "LightMode" = "UniversalForward" }

			ZWrite [_ZWrite]
			Cull [_Cull]
			ZTest [_ZTest]

			HLSLPROGRAM
			#pragma vertex vert
			#pragma fragment frag

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

			struct Attributes { float4 positionOS : POSITION; };
			struct Varyings { float4 positionHCS : SV_POSITION; };

			Varyings vert(Attributes IN) {
				Varyings OUT;
				OUT.positionHCS = TransformObjectToHClip(IN.positionOS.xyz);
				return OUT;
			}

			half4 frag(Varyings IN) : SV_Target {
				// stub
				discard;
				return 0;
			}
			ENDHLSL
		}
	}
}