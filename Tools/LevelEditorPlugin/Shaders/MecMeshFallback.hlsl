// Mirror's Edge Catalyst fallback pixel shader for the level editor and mesh viewer.
//
// Based on the MEC (PROFILE_20160607) path of Frosty's Shaders/MeshFallback.hlsl. The level editor
// plugin registers this as the "20160607" pixel shader, which replaces the one in Shaders.bin. The
// vertex shader is unchanged, so PS_IN must match MeshFallback.hlsl's VS_OUT.
//
// Differences from Frosty's MEC path:
//  - NormalDetailTiling / NormalDetailStrength were applied to the main normal map (stretching it
//    and flattening it to a few percent). They now apply to the NormalDetail texture (t5).
//  - The material's Tiling parameter scales the base UVs.
//  - Without an RSM texture, smoothness/reflectance come from the normal map's z/w only when that
//    texture has those channels (not BC5); otherwise neutral values instead of mirror-smooth.
//  - Alpha test (AlphaTest/Decal shaders) and discard (Invisible shaders), selected from C#.
//
// The C# side that fills MaterialConstants is RenderMesh.AssignFallbackParameters (MEC branch).

#define PROFILE_20160607 1
#include "../../../Shaders/Common.h"

struct PS_IN
{
    float4 Position : SV_Position;
    float3 Normal : NORMAL;
    float3 Tangent : TANGENT;
    float4 Bitangent : BINORMAL;
    float2 TexCoord0 : TEXCOORD0;
    float2 TexCoord1 : TEXCOORD1;
    float2 TexCoord2 : TEXCOORD2;
    float4 Color0 : COLOR0;
    float4 Color1 : COLOR1;
    float3x3 TBN : TANGENTBASIS;
};

struct PS_OUT
{
    float4 GBufferA : SV_Target0;
    float4 GBufferB : SV_Target1;
    float4 GBufferC : SV_Target2;
    float4 GBufferD : SV_Target3;
    float Depth : SV_Target4;
};

cbuffer viewConstants : register(b0)
{
    float4 time;
    float4 screenSize;
    float4x4 viewMatrix;
    float4x4 projMatrix;
    float4x4 viewProjMatrix;
    float4x4 crViewProjMatrix;
    float4x4 prevViewProjMatrix;
    float4x4 crPrevViewProjMatrix;
    float4x3 normalBasisTransforms[6];
    float4 exposureMultipliers;
    float3 cameraPos;
};

cbuffer functionConstants : register(b1)
{
    float4x4 worldMatrix;
    float4 lightProbe[9];
};

// Flags (MecFlags, written as a float by C#)
#define MEC_FLAG_ALPHATEST      1
#define MEC_FLAG_DISCARD        2
#define MEC_FLAG_NORMAL_HAS_RS  4
#define MEC_FLAG_DETAIL_NORMAL  8

cbuffer MaterialConstants : register(b2)
{
    float Usestexture_RSM;   // CustomParam1
    float Usestexture_Ex;    // CustomParam2
    float MecFlags;          // CustomParam3
    float sRGB;
    float4 OverlayColor;
    float4 TintColorA;       // rgb = DiffuseColor, w = has diffuse texture
    float4 TintColorB;       // z = has DiffuseColor
    float4 TintColorC;
    float4 TintColorD;
    float4 MecUv;            // xy = base Tiling
    float4 MecDetail;        // x = NormalDetailTiling, y = NormalDetailStrength
};

TextureCube<float4> texture_normalBasisCubemapTexture : register(t0);
Texture2D<float4> texture_Diffuse : register(t1);
Texture2D<float4> texture_NRS : register(t2);
Texture2D<float4> texture_RSM : register(t3);
Texture2D<float4> texture_Ex : register(t4);
Texture2D<float4> texture_NormalDetail : register(t5);

SamplerState sampler0_s : register(s0);
SamplerState sampler1_s : register(s1);

float3 SafeDeriveNormalZ(float2 n)
{
    return float3(n, sqrt(saturate(1.0f - dot(n, n))));
}

PS_OUT PS_MeshFallback(PS_IN In)
{
    PS_OUT Out = (PS_OUT)0;
    GBufferValues GBuffer = (GBufferValues)0;

    uint flags = (uint)(MecFlags + 0.5f);
    if (flags & MEC_FLAG_DISCARD)
        discard;

    float2 tiling = MecUv.xy;
    if (tiling.x <= 0.0f) tiling.x = 1.0f;
    if (tiling.y <= 0.0f) tiling.y = tiling.x;
    float2 uv = In.TexCoord0 * tiling;

    float hasDiffuseTexture = TintColorA.w;
    float hasDiffuseColor = TintColorB.z;

    float4 Diffuse = texture_Diffuse.Sample(sampler1_s, uv);
    bool alphaTest = (flags & MEC_FLAG_ALPHATEST) != 0 && hasDiffuseTexture > 0.5f;
    if (alphaTest)
        clip(Diffuse.a - 0.5f);

    float4 NRS = texture_NRS.Sample(sampler1_s, uv);
    float4 RSM = texture_RSM.Sample(sampler1_s, uv);
    float4 Ex = texture_Ex.Sample(sampler1_s, uv);

    float3 n = SafeDeriveNormalZ(NRS.xy * 2.0f - 1.0f);

    if (flags & MEC_FLAG_DETAIL_NORMAL)
    {
        float detailTiling = (MecDetail.x > 0.0f) ? MecDetail.x : 1.0f;
        float4 detail = texture_NormalDetail.Sample(sampler1_s, In.TexCoord0 * detailTiling);
        float2 d = (detail.xy * 2.0f - 1.0f) * MecDetail.y;
        n = normalize(float3(n.xy + d, n.z));
    }

    float Reflectance = 0.5f;
    float Smoothness = 0.3f;
    float Metallic = 0.0f;
    if (Usestexture_RSM > 0.5f)
    {
        Reflectance = RSM.x;
        Smoothness = RSM.y;
        Metallic = RSM.z;
    }
    else if (flags & MEC_FLAG_NORMAL_HAS_RS)
    {
        Reflectance = NRS.z;
        Smoothness = NRS.w;
    }
    if (Usestexture_Ex > 0.5f)
        Metallic = Ex.b;

    float3 tint = lerp(float3(1, 1, 1), TintColorA.rgb, hasDiffuseColor);
    float3 baseColor = lerp(float3(1, 1, 1), Diffuse.rgb, hasDiffuseTexture) * tint;
    // Diffuse alpha is used as AO for opaque materials (as in Frosty's MEC path); for alpha-tested
    // materials it is the cut-out mask instead.
    float ao = (hasDiffuseTexture > 0.5f && !alphaTest) ? Diffuse.a : 1.0f;

    float3 Norm = CalcWorldSpaceNormals(n, In.Tangent, In.Bitangent, In.Normal);

    GBuffer.BaseColor = baseColor;
    GBuffer.SpecularMult = 1.0f;
    GBuffer.WorldNormals = Norm;
    GBuffer.Reflectance = Reflectance;
    GBuffer.MaterialAO = ao;
    GBuffer.Smoothness = Smoothness;
    GBuffer.Metallic = Metallic;
    GBuffer.Radiosity = CalculateRadiosity(lightProbe, Norm) * ao;

    PackGBufferValues(GBuffer, Out.GBufferA, Out.GBufferB, Out.GBufferC, Out.GBufferD, normalBasisTransforms, texture_normalBasisCubemapTexture, sampler0_s, exposureMultipliers.x);
    Out.Depth = In.Position.z;

    return Out;
}
