Texture2D<float4> rgbTex	: register(t0);

SamplerState LinearSampler				: register(s0);

struct PixelIn
{
	float4 Position	: SV_Position;
	float2 UV		: TEXCOORD0;
};

// Vertex shader outputs a full screen quad with UV coords without vertex buffer
PixelIn VSMain(uint vertexId : SV_VertexID)
{
	PixelIn result = (PixelIn)0;
	// The input quad is expected in device coordinates 
	// (i.e. 0,0 is center of screen, -1,1 top left, 1,-1 bottom right)
	// Therefore no transformation!
	// The UV coordinates are top-left 0,0, bottom-right 1,1
	result.UV = float2((vertexId << 1) & 2, vertexId & 2);
	result.Position = float4(result.UV * float2(2.0f, -2.0f) + float2(-1.0f, 1.0f), 0.0f, 1.0f);
	return result;
}

float4 PSMain(PixelIn i) : SV_Target
{
	float4 o = rgbTex.Sample(LinearSampler, i.UV);
	return o;
}

