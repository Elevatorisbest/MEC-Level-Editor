// Selection boxes in the level view (see Render/SelectionBoxRenderer.cs).
//
// Input: world-space line segments (line list), already relative to the camera position and in
// render space (X mirrored). The geometry shader turns every segment into a screen-space quad of
// a fixed width in pixels, so the outline reads the same at any distance.
//
// Compiled with fxc by the CompileMecShader target in LevelEditorPlugin.csproj (entry points
// VS_Main, GS_Main, PS_Main) and embedded as resources.

cbuffer SelectionBoxConstants : register(b0)
{
    float4x4 viewProj;   // view at the camera position (camera-relative) * projection
    float4 viewParams;   // x, y = viewport size in pixels, z = line width in pixels, w = alpha
};

struct VS_IN
{
    float3 Position : POSITION;
    float4 Color : COLOR;
};

struct VS_OUT
{
    float4 Position : SV_Position;
    float4 Color : COLOR;
};

VS_OUT VS_Main(VS_IN input)
{
    VS_OUT output;
    // Pull the point slightly towards the camera (0.2% of its distance): the screen position stays
    // the same, but box edges lying on a mesh surface no longer flicker against it in the depth test.
    output.Position = mul(float4(input.Position * 0.998f, 1.0f), viewProj);
    output.Color = input.Color;
    return output;
}

[maxvertexcount(4)]
void GS_Main(line VS_OUT input[2], inout TriangleStream<VS_OUT> stream)
{
    float4 p0 = input[0].Position;
    float4 p1 = input[1].Position;

    // Clip against the near plane (z = 0 in clip space) so that a box around or behind the camera
    // still draws the part in front of it instead of a line flipped through infinity.
    if (p0.z < 0.0f && p1.z < 0.0f)
        return;
    if (p0.z < 0.0f)
        p0 = lerp(p0, p1, p0.z / (p0.z - p1.z));
    else if (p1.z < 0.0f)
        p1 = lerp(p1, p0, p1.z / (p1.z - p0.z));

    float2 halfSize = viewParams.xy * 0.5f;
    float2 s0 = p0.xy / p0.w * halfSize;
    float2 s1 = p1.xy / p1.w * halfSize;

    float2 dir = s1 - s0;
    float len = length(dir);
    dir = (len > 1e-5f) ? dir / len : float2(1.0f, 0.0f);

    float halfWidth = viewParams.z * 0.5f;
    float2 side = float2(-dir.y, dir.x) * halfWidth;
    float2 along = dir * halfWidth; // extend the ends so corners close
    float2 pixelToNdc = 1.0f / halfSize;

    VS_OUT v;
    v.Color = input[0].Color;
    v.Position = p0 + float4((-side - along) * pixelToNdc * p0.w, 0.0f, 0.0f);
    stream.Append(v);
    v.Position = p0 + float4((side - along) * pixelToNdc * p0.w, 0.0f, 0.0f);
    stream.Append(v);

    v.Color = input[1].Color;
    v.Position = p1 + float4((-side + along) * pixelToNdc * p1.w, 0.0f, 0.0f);
    stream.Append(v);
    v.Position = p1 + float4((side + along) * pixelToNdc * p1.w, 0.0f, 0.0f);
    stream.Append(v);
}

float4 PS_Main(VS_OUT input) : SV_Target
{
    return float4(input.Color.rgb, input.Color.a * viewParams.w);
}
