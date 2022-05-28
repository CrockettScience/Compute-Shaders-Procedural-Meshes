#ifndef BEZIER_INCLUDED
#define BEZIER_INCLUDED

struct BezierFrame {
    float3 position;
    float3 right;
    float3 up;
    float3 tangent;
};

#ifdef BEZIER_COMPUTE_SHADER

StructuredBuffer<float4> _TVectors;
float4x4 _ControlPointsMatrix;
float4x4 _ControlQuaternionsMatrix;

float _FrameCorrection;

RWStructuredBuffer<BezierFrame> _CurveFrames;


#define BEZIER_CONTROL_POINTS float4x3(\
_ControlPointsMatrix._m00, _ControlPointsMatrix._m01, _ControlPointsMatrix._m02, \
_ControlPointsMatrix._m10, _ControlPointsMatrix._m11, _ControlPointsMatrix._m12, \
_ControlPointsMatrix._m20, _ControlPointsMatrix._m21, _ControlPointsMatrix._m22, \
_ControlPointsMatrix._m30, _ControlPointsMatrix._m31, _ControlPointsMatrix._m32)

#define BEZIER_MATRIX float4x4( \
-1,  3, -3,  1, \
3, -6,  3,  0, \
-3,  3,  0,  0, \
1,  0,  0,  0)

#define DERIVATIVE_MATRIX float3x4( \
-3, 9, -9, 3,\
6, -12, 6, 0,\
-3, 3, 0, 0)

//SEE https://gist.github.com/mattatz/40a91588d5fb38240403f198a938a593
#define QUATERNION_IDENTITY float4(0, 0, 0, 1)

float4 Slerp(float4 a, float4 b, float t)
{
    if (length(a) == 0.0)
    {
        if (length(b) == 0.0)
        {
            return QUATERNION_IDENTITY;
        }
        return b;
    }
    else if (length(b) == 0.0)
    {
        return a;
    }

    float cosHalfAngle = a.w * b.w + dot(a.xyz, b.xyz);

    if (cosHalfAngle >= 1.0 || cosHalfAngle <= -1.0)
    {
        return a;
    }
    else if (cosHalfAngle < 0.0)
    {
        b.xyz = -b.xyz;
        b.w = -b.w;
        cosHalfAngle = -cosHalfAngle;
    }

    float blendA;
    float blendB;
    if (cosHalfAngle < 0.99)
    {
        float halfAngle = acos(cosHalfAngle);
        float sinHalfAngle = sin(halfAngle);
        float oneOverSinHalfAngle = 1.0 / sinHalfAngle;
        blendA = sin(halfAngle * (1.0 - t)) * oneOverSinHalfAngle;
        blendB = sin(halfAngle * t) * oneOverSinHalfAngle;
    }
    else
    {
        blendA = 1.0 - t;
        blendB = t;
    }

    float4 result = float4(blendA * a.xyz + blendB * b.xyz, blendA * a.w + blendB * b.w);
    if (length(result) > 0.0)
    {
        return normalize(result);
    }
    return QUATERNION_IDENTITY;
}

float4 QMul(float4 q1, float4 q2)
{
    return float4(
        q2.xyz * q1.w + q1.xyz * q2.w + cross(q1.xyz, q2.xyz),
        q1.w * q2.w - dot(q1.xyz, q2.xyz)
    );
}

float3 QMul(float3 v, float4 q)
{
    float4 q_c = q * float4(-1, -1, -1, 1);
    return QMul(q, QMul(float4(v, 0), q_c)).xyz;
}

float4 QuaternionBezier(float4x4 qM, float t)
{
    float4 q01 = Slerp(qM._m00_m01_m02_m03, qM._m10_m11_m12_m13, t);
    float4 q12 = Slerp(qM._m10_m11_m12_m13, qM._m20_m21_m22_m23, t);
    float4 q23 = Slerp(qM._m20_m21_m22_m23, qM._m30_m31_m32_m33, t);
    
    float4 q012 = Slerp(q01, q12, t);
    float4 q123 = Slerp(q12, q23, t);
    
    return Slerp(q012, q123, t);
}

#endif

#ifdef BEZIER_SURFACE_SHADER

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/SurfaceInput.hlsl"

//The same structure we initialise in compute, but not rewriteable
struct Vertex{
    //Size: float x 13, int x 1
    float3 positionOS;
    float3 normalOS;
    float2 uv;
    float4 color;
    int frameOffset;
};
		
struct Triangle {
    Vertex verts[3];
};

StructuredBuffer<BezierFrame> _CurveFrames;
StructuredBuffer<Triangle> _SrcMesh;
StructuredBuffer<uint2> _BMeshIndex;

Vertex ImportVertexFromSrcMesh(uint vertexID){
    Vertex vert = (Vertex) 0;
    
    //id.x is our t value; id.y is our src mesh triangle index
    uint2 id = _BMeshIndex[vertexID];
    
    Vertex srcVert = _SrcMesh[id.y].verts[vertexID % 3];
    BezierFrame frame = _CurveFrames[id.x + srcVert.frameOffset];
    
    //Translate some of our properties into our curved space
    vert.positionOS =   frame.position + 
                        srcVert.positionOS.x * frame.right +
                        srcVert.positionOS.y * frame.up +
                        srcVert.positionOS.z * frame.tangent;
                       
    vert.normalOS = normalize(srcVert.normalOS.x * frame.right +
                              srcVert.normalOS.y * frame.up +
                              srcVert.normalOS.z * frame.tangent);
                              
    vert.uv = srcVert.uv;
    vert.color = srcVert.color;
                        
    return vert;
}

// Textures, Samplers & Global Properties
// (note, BaseMap, BumpMap and EmissionMap is being defined by the SurfaceInput.hlsl include)
TEXTURE2D(_SpecGlossMap); 	SAMPLER(sampler_SpecGlossMap);

// Functions
half4 SampleSpecularSmoothness(float2 uv, half alpha, half4 specColor, TEXTURE2D_PARAM(specMap, sampler_specMap)) {
    half4 specularSmoothness = half4(0.0h, 0.0h, 0.0h, 1.0h);
    #ifdef _SPECGLOSSMAP
        specularSmoothness = SAMPLE_TEXTURE2D(specMap, sampler_specMap, uv) * specColor;
    #elif defined(_SPECULAR_COLOR)
        specularSmoothness = specColor;
    #endif

    #if UNITY_VERSION >= 202120 // or #if SHADER_LIBRARY_VERSION_MAJOR < 12, but that versioning method is deprecated for newer versions
        // v12 is changing this, so it's calculated later. Likely so that smoothness value stays 0-1 so it can display better for debug views.
        #ifdef _GLOSSINESS_FROM_BASE_ALPHA
            specularSmoothness.a = exp2(10 * alpha + 1);
        #else
            specularSmoothness.a = exp2(10 * specularSmoothness.a + 1);
        #endif
    #endif
    return specularSmoothness;
}

#if SHADER_LIBRARY_VERSION_MAJOR < 9

float3 GetWorldSpaceViewDir(float3 positionWS) {
	if (unity_OrthoParams.w == 0) {
		// Perspective
		return _WorldSpaceCameraPos - positionWS;
	} else {
		// Orthographic
		float4x4 viewMat = GetWorldToViewMatrix();
		return viewMat[2].xyz;
	}
}

half3 GetWorldSpaceNormalizeViewDir(float3 positionWS) {
	float3 viewDir = GetWorldSpaceViewDir(positionWS);
	if (unity_OrthoParams.w == 0) {
		// Perspective
		return half3(normalize(viewDir));
	} else {
		// Orthographic
		return half3(viewDir);
	}
}
#endif

#endif

#endif