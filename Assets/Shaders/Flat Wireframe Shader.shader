Shader "Custom/Flat Wireframe" {

	Properties {
		_Color ("Tint", Color) = (1, 1, 1, 1)
		_MainTex ("Albedo", 2D) = "white" {}

		_Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

		_WireframeColor ("Wireframe Color", Color) = (0, 0, 0)
		_WireframeSmoothing ("Wireframe Smoothing", Range(0, 10)) = 1
		_WireframeThickness ("Wireframe Thickness", Range(0, 10)) = 1
		[Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 2 //"Back"
		[Enum(Off,0,On,1)] _ZWrite ("ZWrite", Float) = 0.0 //"Off"
		[Enum(UnityEngine.Rendering.CompareFunction)] _ZTest ("ZTest", Float) = 5 //"LessEqual"
	}


	SubShader {

		Pass {

			Blend SrcAlpha OneMinusSrcAlpha
			ZWrite [_ZWrite]
			Cull [_Cull]
			ZTest [_ZTest]

			CGPROGRAM

			#pragma vertex MyVertexProgram
			#pragma fragment MyFragmentProgram
			#pragma geometry MyGeometryProgram

			#include "MyFlatWireframe.cginc"

			ENDCG
		}
	}

	CustomEditor "MyLightingShaderGUI"
}