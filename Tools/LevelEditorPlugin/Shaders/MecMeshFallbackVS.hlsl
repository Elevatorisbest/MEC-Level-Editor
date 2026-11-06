// Mirror's Edge Catalyst fallback vertex shader with optional instancing.
//
// Based on VS_MeshFallback in Frosty's Shaders/MeshFallback.hlsl (compiled there for MEC with
// OPTIMIZE=1, i.e. without the TBN output). Registered by the level editor plugin as the
// "20160607" vertex shader. VS_IN/VS_OUT must stay identical to MeshFallback.hlsl.
//
// Instancing: when instanceConstants.instanced != 0 the world matrix of each instance comes from
// instanceMatrices (4 float4 rows per instance, starting at instanceBase) instead of
// functionConstants.worldMatrix. The constant buffer at b4 is only bound for instanced draws; an
// unbound constant buffer reads as zero, so every other draw takes the normal path.

#define PROFILE_20160607 1
#include "../../../Shaders/Common.h"

struct VS_IN
{
    float3 Position : POSITION;
    float4 Normal : NORMAL;
    float4 Tangent : TANGENT;
    float4 Bitangent : BINORMAL;
    float2 TexCoord0 : TEXCOORD0;
    float2 TexCoord1 : TEXCOORD1;
    float2 TexCoord2 : TEXCOORD2;
    float4 Color0 : COLOR0;
    float4 Color1 : COLOR1;
    uint4 BoneIndices : BLENDINDICES0;
    uint4 BoneIndices2 : BLENDINDICES1;
    float4 BoneWeights : BLENDWEIGHT0;
    float4 BoneWeights2 : BLENDWEIGHT1;
    uint TangentSpace : TANGENTSPACE;
    uint InstanceId : SV_InstanceID;
};

struct VS_OUT
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

cbuffer instanceConstants : register(b4)
{
    uint instanceBase;
    uint instanced;
    uint2 instancePadding;
};

Buffer<float4> bonePartMatrices : register(t0);
Buffer<float4> instanceMatrices : register(t4);

float4x4 GetWorldMatrix(uint instanceId)
{
    if (instanced == 0)
        return worldMatrix;

    uint i = (instanceBase + instanceId) * 4;
    // rows of the (row-vector) world matrix, as written by InstancedMeshBatcher
    return float4x4(instanceMatrices[i], instanceMatrices[i + 1], instanceMatrices[i + 2], instanceMatrices[i + 3]);
}

float3x4 getBoneMatrix(int Index)
{
    float boneCount = bonePartMatrices[0].x;
    if (Index & 0x8000)
        Index = (Index & 0x7FFF) + boneCount;

    float4 A = bonePartMatrices[Index * 3 + 1];
    float4 B = bonePartMatrices[Index * 3 + 2];
    float4 C = bonePartMatrices[Index * 3 + 3];

    return float3x4(A, B, C);
}

#define SHADER_BONES_PER_VERTEX 8

void GetBones(VS_IN Input, out uint BoneIndices[SHADER_BONES_PER_VERTEX], out float BoneWeights[SHADER_BONES_PER_VERTEX])
{
    BoneIndices[0] = Input.BoneIndices.x;
    BoneIndices[1] = Input.BoneIndices.y;
    BoneIndices[2] = Input.BoneIndices.z;
    BoneIndices[3] = Input.BoneIndices.w;
    BoneIndices[4] = Input.BoneIndices2.x;
    BoneIndices[5] = Input.BoneIndices2.y;
    BoneIndices[6] = Input.BoneIndices2.z;
    BoneIndices[7] = Input.BoneIndices2.w;
    BoneWeights[0] = Input.BoneWeights.x;
    BoneWeights[1] = Input.BoneWeights.y;
    BoneWeights[2] = Input.BoneWeights.z;
    BoneWeights[3] = Input.BoneWeights.w;
    BoneWeights[4] = Input.BoneWeights2.x;
    BoneWeights[5] = Input.BoneWeights2.y;
    BoneWeights[6] = Input.BoneWeights2.z;
    BoneWeights[7] = Input.BoneWeights2.w;
}

float4 TransformPosition(VS_IN Input, float4x4 world)
{
    uint BoneIndices[SHADER_BONES_PER_VERTEX];
    float BoneWeights[SHADER_BONES_PER_VERTEX];
    GetBones(Input, BoneIndices, BoneWeights);

    float3 skinnedPosition = float3(0, 0, 0);
    for (int i = 0; i < SHADER_BONES_PER_VERTEX; i++)
        skinnedPosition += mul(getBoneMatrix(BoneIndices[i]), float4(Input.Position.xyz, 1.0f)).xyz * BoneWeights[i];

    float3 worldPosition = mul(float4(skinnedPosition, 1.0f), world).xyz - cameraPos;
    return mul(float4(worldPosition, 1.0f), crViewProjMatrix);
}

float3 TransformTBN(VS_IN Input, float3 Value, float4x4 world)
{
    uint BoneIndices[SHADER_BONES_PER_VERTEX];
    float BoneWeights[SHADER_BONES_PER_VERTEX];
    GetBones(Input, BoneIndices, BoneWeights);

    float3 skinnedValue = float3(0, 0, 0);
    for (int i = 0; i < SHADER_BONES_PER_VERTEX; i++)
        skinnedValue += mul((float3x3)getBoneMatrix(BoneIndices[i]), Value).xyz * BoneWeights[i];

    // same as Frosty: mul(matrix, vector) with the world matrix here
    return mul((float3x3)world, normalize(skinnedValue));
}

VS_OUT VS_Main(VS_IN In)
{
    VS_OUT Out = (VS_OUT)0;
    float4x4 world = GetWorldMatrix(In.InstanceId);

    Out.Position = TransformPosition(In, world);
    Out.Normal = TransformTBN(In, In.Normal.xyz, world);
    Out.Tangent = TransformTBN(In, In.Tangent.xyz, world);
    Out.Bitangent.xyz = TransformTBN(In, In.Bitangent.xyz, world);
    Out.Bitangent.w = In.Bitangent.w;

    Out.TexCoord0 = In.TexCoord0;
    Out.TexCoord1 = In.TexCoord1;
    Out.TexCoord2 = In.TexCoord2;
    Out.Color0 = In.Color0;
    Out.Color1 = In.Color1;

    return Out;
}
