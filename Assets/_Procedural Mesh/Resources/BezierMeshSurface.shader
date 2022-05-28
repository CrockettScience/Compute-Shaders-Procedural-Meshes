Shader "CrockettScience/BezierMeshSurface"
{
    //This shader is a placeholder for testing purposes, adapted from a template (https://github.com/Cyanilux/URP_ShaderCodeTemplates/blob/main/URP_SimpleLitTemplate.shader)
    //Normal/Bump maps and Specular/Gloss are disabled, and lightmap UVs are not integrated

    Properties {
		[MainTexture] _BaseMap("Base Map (RGB)", 2D) = "white" {}
		[MainColor]   _BaseColor("Base Color", Color) = (0, 0, 0, 0)
		
		//Not supported yet
		//[Toggle(_NORMALMAP)] _NormalMapToggle ("Normal Mapping", Float) = 0
		//[NoScaleOffset] _BumpMap("Normal Map", 2D) = "bump" {}

		[HDR] _EmissionColor("Emission Color", Color) = (0,0,0)
		[Toggle(_EMISSION)] _Emission ("Emission", Float) = 0
		[NoScaleOffset]_EmissionMap("Emission Map", 2D) = "white" {}

		[Toggle(_ALPHATEST_ON)] _AlphaTestToggle ("Alpha Clipping", Float) = 0
		_Cutoff ("Alpha Cutoff", Float) = 0.5

		//[Toggle(_SPECGLOSSMAP)] _SpecGlossMapToggle ("Use Specular Gloss Map", Float) = 0
		//_SpecColor("Specular Color", Color) = (0.5, 0.5, 0.5, 0.5)
		//_SpecGlossMap("Specular Map", 2D) = "white" {}
		//[Toggle(_GLOSSINESS_FROM_BASE_ALPHA)] _GlossSource ("Glossiness Source, from Albedo Alpha (if on) vs from Specular (if off)", Float) = 0
		//_Smoothness("Smoothness", Range(0.0, 1.0)) = 0.5
	}
	SubShader {
		Tags {
			"RenderPipeline"="UniversalPipeline"
			"RenderType"="Opaque"
			"Queue"="Geometry"
		}

		HLSLINCLUDE
		#pragma target 4.0
		
		#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

		CBUFFER_START(UnityPerMaterial)
		float4 _BaseMap_ST;
		float4 _BaseColor;
		float4 _EmissionColor;
		float4 _SpecColor;
		float _Cutoff;
		float _Smoothness;
		CBUFFER_END
			
		#define BEZIER_SURFACE_SHADER
		ENDHLSL

		Pass {
			Name "ForwardLit"
			Tags { "LightMode"="UniversalForward" }

			HLSLPROGRAM
			#pragma vertex LitPassVertex
			#pragma fragment LitPassFragment

			// Material Keywords
			//#pragma shader_feature_local _NORMALMAP -> not supported
			#pragma shader_feature_local_fragment _EMISSION
			#pragma shader_feature_local _RECEIVE_SHADOWS_OFF
			#pragma shader_feature_local_fragment _ALPHATEST_ON
			#pragma shader_feature_local_fragment _ALPHAPREMULTIPLY_ON
			#pragma shader_feature_local_fragment _ _SPECGLOSSMAP
			#define _SPECULAR_COLOR

			// URP Keywords
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS
			#pragma multi_compile _ _MAIN_LIGHT_SHADOWS_CASCADE

			#pragma multi_compile _ _SHADOWS_SOFT
			#pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
			#pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
			#pragma multi_compile _ LIGHTMAP_SHADOW_MIXING 
			#pragma multi_compile _ SHADOWS_SHADOWMASK

			// Unity Keywords
			//#pragma multi_compile _ LIGHTMAP_ON -> not supported
			//#pragma multi_compile _ DIRLIGHTMAP_COMBINED -> not supported
			#pragma multi_compile_fog

			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

			struct Attributes {
			    uint vertexID : SV_VertexID;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};

			struct Varyings {
				float4 positionCS 					: SV_POSITION;
				float2 uv		    				: TEXCOORD0;
			    float3 positionWS					: TEXCOORD2;
			    half3 normalWS					    : TEXCOORD3;
			    
			    #ifdef _ADDITIONAL_LIGHTS_VERTEX
			    	half4 fogFactorAndVertexLight	: TEXCOORD6;
			    #else
			    	half  fogFactor					: TEXCOORD6;
			    #endif

			    #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
			    	float4 shadowCoord 				: TEXCOORD7;
			    #endif

				float4 color						: COLOR;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				UNITY_VERTEX_OUTPUT_STEREO
			};
			
            #include "BezierInclude.hlsl"

            void InitializeSurfaceData(Varyings IN, out SurfaceData surfaceData){
                surfaceData = (SurfaceData)0;
            
                half4 baseMap = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, IN.uv);
            
                #ifdef _ALPHATEST_ON
                    clip(baseMap.a - _Cutoff);
                #endif
            
                half4 diffuse = baseMap * _BaseColor;
                surfaceData.albedo = diffuse.rgb;
                surfaceData.normalTS = SampleNormal(IN.uv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap));
                surfaceData.emission = SampleEmission(IN.uv, _EmissionColor.rgb, TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap));
                surfaceData.occlusion = 1.0; 
            
                half4 specular = SampleSpecularSmoothness(IN.uv, diffuse.a, _SpecColor, TEXTURE2D_ARGS(_SpecGlossMap, sampler_SpecGlossMap));
                surfaceData.specular = specular.rgb;
                surfaceData.smoothness = specular.a * _Smoothness;
            }
            
            float _RecieveShadows;
            
            void InitializeInputData(Varyings input, half3 normalTS, out InputData inputData) {
                inputData = (InputData)0;
            
                inputData.positionWS = input.positionWS;
            
                #ifdef _NORMALMAP
                    half3 viewDirWS = half3(input.normalWS.w, input.tangentWS.w, input.bitangentWS.w);
                    inputData.normalWS = TransformTangentToWorld(normalTS,half3x3(input.tangentWS.xyz, input.bitangentWS.xyz, input.normalWS.xyz));
                #else
                    half3 viewDirWS = GetWorldSpaceNormalizeViewDir(inputData.positionWS);
                    inputData.normalWS = input.normalWS;
                #endif
            
                inputData.normalWS = NormalizeNormalPerPixel(inputData.normalWS);
            
                viewDirWS = SafeNormalize(viewDirWS);
                inputData.viewDirectionWS = viewDirWS;
            
                #if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
                    inputData.shadowCoord = _RecieveShadows ? input.shadowCoord : float4(0, 0, 0, 0);
                #elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
                    inputData.shadowCoord = _RecieveShadows ? TransformWorldToShadowCoord(inputData.positionWS) : float4(0, 0, 0, 0);
                #else
                    inputData.shadowCoord = float4(0, 0, 0, 0);
                #endif
            
                // Fog
                #ifdef _ADDITIONAL_LIGHTS_VERTEX
                    inputData.fogCoord = input.fogFactorAndVertexLight.x;
                    inputData.vertexLighting = input.fogFactorAndVertexLight.yzw;
                #else
                    inputData.fogCoord = input.fogFactor;
                    inputData.vertexLighting = half3(0, 0, 0);
                #endif
            
                //inputData.bakedGI = SAMPLE_GI(input.lightmapUV, input.vertexSH, inputData.normalWS);
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);
                inputData.shadowMask = SAMPLE_SHADOWMASK(input.lightmapUV);
            }

			Varyings LitPassVertex(Attributes i) {
				Varyings o = (Varyings)0;

			    UNITY_SETUP_INSTANCE_ID(i);
			    UNITY_TRANSFER_INSTANCE_ID(i, o);
			    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
				
				Vertex vert = ImportVertexFromSrcMesh(i.vertexID);

				VertexPositionInputs positionInputs = GetVertexPositionInputs(vert.positionOS);
				
				VertexNormalInputs normalInputs = GetVertexNormalInputs(vert.normalOS.xyz);

				o.positionCS = positionInputs.positionCS;
				o.positionWS = positionInputs.positionWS;

				half3 viewDirWS = GetWorldSpaceViewDir(positionInputs.positionWS);
				half3 vertexLight = VertexLighting(positionInputs.positionWS, normalInputs.normalWS);
				half fogFactor = ComputeFogFactor(positionInputs.positionCS.z);
					
				o.normalWS = NormalizeNormalPerVertex(normalInputs.normalWS);

				#ifdef _ADDITIONAL_LIGHTS_VERTEX
					o.fogFactorAndVertexLight = half4(fogFactor, vertexLight);
				#else
					o.fogFactor = fogFactor;
				#endif

				#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
					o.shadowCoord = GetShadowCoord(positionInputs);
				#endif

				o.uv = TRANSFORM_TEX(vert.uv, _BaseMap);
				o.color = vert.color;
				return o;
			}
			
			half4 LitPassFragment(Varyings i) : SV_Target {
				UNITY_SETUP_INSTANCE_ID(i);
				UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
				
				SurfaceData surfaceData;
				InitializeSurfaceData(i, surfaceData);

				InputData inputData;
				InitializeInputData(i, surfaceData.normalTS, inputData);
				
				half4 color = UniversalFragmentBlinnPhong(inputData, surfaceData.albedo, half4(0, 0, 0, 0), 
				surfaceData.smoothness, surfaceData.emission, surfaceData.alpha);

				color.rgb = MixFog(color.rgb, inputData.fogCoord);
				return color;
			}
			ENDHLSL
		}
		
		Pass {
			Name "ShadowCaster"
			Tags { "LightMode"="ShadowCaster" }

			ZWrite On
			ZTest LEqual

			HLSLPROGRAM
			#pragma vertex ShadowPassVertex
			#pragma fragment ShadowPassFragment

			// Material Keywords
			#pragma shader_feature_local_fragment _ALPHATEST_ON
			#pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

			// Universal Pipeline Keywords
			#pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW

			#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
            
            float3 _LightDirection;
			
			struct Attributes {
			
			    uint vertexID : SV_VertexID;
				UNITY_VERTEX_INPUT_INSTANCE_ID
			};
			
			struct Varyings
            {
                float2 uv           : TEXCOORD0;
                float4 positionCS   : SV_POSITION;
				UNITY_VERTEX_INPUT_INSTANCE_ID
				UNITY_VERTEX_OUTPUT_STEREO
            };
			
            #include "BezierInclude.hlsl"
			
			float4 GetShadowPositionHClip(Vertex vert)
            {
                float3 positionWS = TransformObjectToWorld(vert.positionOS.xyz);
                float3 normalWS = TransformObjectToWorldNormal(vert.normalOS);
            
                float4 positionCS = TransformWorldToHClip(ApplyShadowBias(positionWS, normalWS, _LightDirection));
            
            #if UNITY_REVERSED_Z
                positionCS.z = min(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #else
                positionCS.z = max(positionCS.z, positionCS.w * UNITY_NEAR_CLIP_VALUE);
            #endif
            
                return positionCS;
            }
			
			Varyings ShadowPassVertex(Attributes i)
			{
			    Varyings o = (Varyings) 0;
			    
			    UNITY_SETUP_INSTANCE_ID(i);
			    UNITY_TRANSFER_INSTANCE_ID(i, o);
			    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
				
				Vertex vert = ImportVertexFromSrcMesh(i.vertexID);
            
				o.uv = TRANSFORM_TEX(vert.uv, _BaseMap);
                o.positionCS = GetShadowPositionHClip(vert);
                return o;
			}
			
			half4 ShadowPassFragment(Varyings input) : SV_TARGET
            {
                Alpha(SampleAlbedoAlpha(input.uv, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap)).a, _BaseColor, _Cutoff);
                return 0;
            }
			ENDHLSL
		}

		Pass {
			Name "DepthOnly"
			Tags { "LightMode"="DepthOnly" }

			ColorMask 0
			ZWrite On
			ZTest LEqual

			HLSLPROGRAM
			#pragma vertex DepthOnlyVertex
			#pragma fragment DepthOnlyFragment

			// Material Keywords
			#pragma shader_feature_local_fragment _ALPHATEST_ON
			#pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

			#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/Shaders/DepthOnlyPass.hlsl"
			
			ENDHLSL
		}

		// DepthNormals, used for SSAO & other custom renderer features that request it
		Pass {
			Name "DepthNormals"
			Tags { "LightMode"="DepthNormals" }

			ZWrite On
			ZTest LEqual

			HLSLPROGRAM
			#pragma vertex DepthNormalsVertex
			#pragma fragment DepthNormalsFragment

			// Material Keywords
			#pragma shader_feature_local _NORMALMAP
			#pragma shader_feature_local_fragment _ALPHATEST_ON
			#pragma shader_feature_local_fragment _SMOOTHNESS_TEXTURE_ALBEDO_CHANNEL_A

			#include "Packages/com.unity.render-pipelines.core/ShaderLibrary/CommonMaterial.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"
			#include "Packages/com.unity.render-pipelines.universal/Shaders/DepthNormalsPass.hlsl"
			
			ENDHLSL
		}
	}
}
