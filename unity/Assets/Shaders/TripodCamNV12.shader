// Turns the tripod's picture (RGB, width x height) into an NV12 frame for the video encoder, on the GPU.
// The target is an R8 texture of width x (height * 1.5), read back as-is:
//   rows 0 .. height-1             the Y plane (brightness), top of the picture first;
//   rows height .. height*1.5-1    the UV plane (colour), one U,V pair per 2x2 pixels.
// Colours are BT.709, limited range (what players expect from an H.264 video).
// _Size = (width, height, 1 when the source returns linear values that need encoding back to sRGB, 0).
Shader "Hidden/TripodCam/NV12"
{
    Properties { _MainTex ("Source", 2D) = "black" {} }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _Size;

            float3 Source(float2 uv)
            {
                float3 c = saturate(tex2Dlod(_MainTex, float4(uv, 0, 0)).rgb);
                if (_Size.z > 0.5)
                    c = c <= 0.0031308 ? c * 12.92 : 1.055 * pow(c, 1.0 / 2.4) - 0.055;
                return c;
            }

            float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }

            float4 frag(v2f_img i) : SV_Target
            {
                float w = _Size.x, h = _Size.y;
                float row = floor(i.uv.y * h * 1.5);
                float col = floor(i.uv.x * w);
                if (row < h)
                {
                    float3 c = Source(float2((col + 0.5) / w, 1 - (row + 0.5) / h));
                    return (16 + 219 * Luma(c)) / 255;
                }
                // One colour pair per 2x2 block: the average of its four pixels.
                float r = row - h;
                float2 centre = float2((2 * floor(col * 0.5) + 1) / w, 1 - (2 * r + 1) / h);
                float2 t = float2(0.5 / w, 0.5 / h);
                float3 c = (Source(centre - t) + Source(centre + t) + Source(centre + float2(t.x, -t.y)) + Source(centre + float2(-t.x, t.y))) * 0.25;
                float y = Luma(c);
                float chroma = fmod(col, 2) < 0.5 ? (c.b - y) / 1.8556 : (c.r - y) / 1.5748;
                return (128 + 224 * chroma) / 255;
            }
            ENDCG
        }
    }
}
