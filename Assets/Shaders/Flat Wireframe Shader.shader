Shader "Custom/Flat Wireframe" {

	Properties {
		_Color ("Tint", Color) = (1, 1, 1, 1)
		_MainTex ("Albedo", 2D) = "white" {}

		_Cutoff ("Alpha Cutoff", Range(0, 1)) = 0.5

		_WireframeColor ("Wireframe Color", Color) = (0, 0, 0)
		_WireframeSmoothing ("Wireframe Smoothing", Range(0, 10)) = 1
		_WireframeThickness ("Wireframe Thickness", Range(0, 10)) = 1
	}


	SubShader {

		Pass {

			Blend SrcAlpha OneMinusSrcAlpha
			ZWrite Off

			CGPROGRAM

			//#pragma target 4.0

			#pragma vertex MyVertexProgram
			#pragma fragment MyFragmentProgram
			#pragma geometry MyGeometryProgram

			#define FORWARD_BASE_PASS

			#include "MyFlatWireframe.cginc"

			ENDCG
		}
	}

	CustomEditor "MyLightingShaderGUI"
}