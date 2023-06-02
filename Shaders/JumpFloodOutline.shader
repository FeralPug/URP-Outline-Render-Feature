Shader "FeralPug/URP/Outlines/JumpFlood"
{
    //https://gist.github.com/bgolus/a18c1a3fc9af2d73cc19169a809eb195

    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
        SubShader
    {
        Tags {
            "PreviewType" = "Plane"
            "RenderPipeline" = "UniversalPipeline"
        }

        Cull Off
        Zwrite Off
        ZTest Always

        HLSLINCLUDE

        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        #pragma exclude_renderers gles gles3 glcore
        #pragma target 4.5

        CBUFFER_START(UnityPerMaterial)
            float4 _MainTex_TexelSize;
            float4 _MainTex_ST;
        CBUFFER_END

        //for a 16 bit signed int 32767 is the highest number
        //so for a R16_G16_SNorm texture this is the 1.0 - the smallest delta
        //we can use this to convert values to be between -1.0 <= x < 1.0
        //which is needed for storing distances in the Norm Texture (must be -1 to 1)
        //this is literally just doing * 2 - 1 on the pixel pos so that it can fit in the texture
        #define SNORM16_MAX_FLOAT_MINUS_EPSILON ((float)(32768-2) / (float)(32768-1))
        #define FLOOD_ENCODE_OFFSET float2(1.0, SNORM16_MAX_FLOAT_MINUS_EPSILON)
        #define FLOOD_ENCODE_SCALE float2(2.0, 1.0 + SNORM16_MAX_FLOAT_MINUS_EPSILON)

        #define FLOOD_NULL_POS -1.0
        #define FLOOD_NULL_POS_FLOAT2 float2(FLOOD_NULL_POS, FLOOD_NULL_POS)
        
        ENDHLSL


        Pass //0
        {
            Name "INNERSTENCIL"

            Stencil{
                Ref 1
                ReadMask 1
                WriteMask 1
                Comp NotEqual
                Pass Replace
            }

            ZTest LEqual

            ColorMask 0
            Blend Zero One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            float4 vert (Attributes IN) : SV_POSITION
            {
                VertexPositionInputs positionInputs = GetVertexPositionInputs(IN.positionOS.xyz);

                return positionInputs.positionCS;
            }

            //null frag, just writing to stencil
            void frag(){}
            
            ENDHLSL
        }
    
        Pass //1
        {
            Name "BUFFERFILL"

            ZTest LEqual

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            float4 vert(Attributes IN) : SV_POSITION
            {
                VertexPositionInputs positionInputs = GetVertexPositionInputs(IN.positionOS.xyz);

                float4 positionCS = positionInputs.positionCS;

                //Flip Rendering "upside down" in non OpenGL to make things easier later
                //you'll notice none of the later passes need to pass UVs
                #ifdef UNITY_UV_STARTS_AT_TOP
                    positionCS.y = -positionCS.y;
                #endif

                return positionCS;
            }

            half frag() : SV_TARGET
            {
                return 1.0;
            }

            ENDHLSL
        }

        Pass //2
        {
            Name "JUMPFLOODINIT"

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            Varyings vert(Attributes IN)
            {
                Varyings OUT;

                VertexPositionInputs positionInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                OUT.positionCS = positionInputs.positionCS;

                return OUT;
            }


            float2 frag(Varyings IN) : SV_TARGET
            {
                //integer pixel pos
                int2 uvInt = IN.positionCS.xy;

                //sample silhouette texture for sobel
                half3x3 values;
                UNITY_UNROLL
                for (int u = 0; u < 3; u++) {
                    UNITY_UNROLL
                    for (int v = 0; v < 3; v++){
                        uint2 sampleUV = clamp(uvInt + int2(u - 1, v - 1), int2(0, 0), (int2)_MainTex_TexelSize.zw - 1);
                        //values[u][v] = SAMPLE_TEXTURE2D_LOD(_MainTex, sampler_MainTex, sampleUV, 0);
                        values[u][v] = _MainTex.Load(int3(sampleUV, 0)).r;
                    }
                }

                //calculate output position for this pixel
                float2 outPos = IN.positionCS.xy * abs(_MainTex_TexelSize.xy) * FLOOD_ENCODE_SCALE - FLOOD_ENCODE_OFFSET;

                //interior, return position
                if (values._m11 >= 0.99) {
                    return outPos;
                }

                //exterior, return null pos
                if (values._m11 < 0.01) {
                    return FLOOD_NULL_POS_FLOAT2;
                }

                //sobel to estimate edge direction
                float2 dir = -float2(
                    values[0][0] + values[0][1] * 2.0 + values[0][2] - values[2][0] - values[2][1] * 2.0 - values[2][2],
                    values[0][0] + values[1][0] * 2.0 + values[2][0] - values[0][2] - values[1][2] * 2.0 - values[2][2]
                );

                //if dir length is small, this is either a sub pixel dot or line
                //no way to estimate sub pixel edge, so output position
                if (abs(dir.x) <= 0.005 && abs(dir.y) <= 0.005) {
                    return outPos;
                }

                //normalize direction
                dir = normalize(dir);

                //sub pixel offset
                float2 offset = dir * (1.0 - values._m11);

                //output encoded offset position
                return (IN.positionCS.xy + offset) * abs(_MainTex_TexelSize.xy) * FLOOD_ENCODE_SCALE - FLOOD_ENCODE_OFFSET;
            }

            ENDHLSL
        }

        Pass //3
        {
            Name "JUMPFLOOD"

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            struct Attributes {
                float4 positionOS : POSITION;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            int _JumpFloodStepWidth;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionCS = GetVertexPositionInputs(IN.positionOS.xyz).positionCS;
                return OUT;
            }

            float2 frag(Varyings IN) : SV_TARGET{
                //integer pixel pos
                int2 uvInt = int2(IN.positionCS.xy);

                //initialize best distance at infinity
                float bestDist = 1.#INF;
                float2 bestCoord;

                //jump samples
                UNITY_UNROLL
                for (int u = -1; u <= 1; u++) {
                    UNITY_UNROLL
                    for (int v = -1; v <= 1; v++) {
                        //calc offset sample pos
                        int2 offsetUV = uvInt + int2(u, v) * _JumpFloodStepWidth;

                        //.Load() acts funny when sampling outside of bounds, so dont
                        offsetUV = clamp(offsetUV, int2(0, 0), (int2)_MainTex_TexelSize.zw - 1);

                        //decode position from buffer
                        float2 offsetPos = (_MainTex.Load(int3(offsetUV, 0)).rg + FLOOD_ENCODE_OFFSET) * _MainTex_TexelSize.zw / FLOOD_ENCODE_SCALE;

                        //the offset from current position
                        float2 disp = IN.positionCS.xy - offsetPos;

                        //square distance
                        float dist = dot(disp, disp);

                        //if offset position isn't a null position or is closer than the best
                        //set as the new best and store the position
                        if (offsetPos.y != FLOOD_NULL_POS && dist < bestDist) {
                            bestDist = dist;
                            bestCoord = offsetPos;
                        }
                    }
                }
                //if not valid best distance output null position, otherwise output encoded position
                return isinf(bestDist) ? FLOOD_NULL_POS_FLOAT2 : bestCoord * _MainTex_TexelSize.xy * FLOOD_ENCODE_SCALE - FLOOD_ENCODE_OFFSET;
            }
            ENDHLSL
        }

        Pass //4
        {
            Name "JUMPFLOOD_SINGLEAXIS"

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            struct Attributes {
                float4 positionOS : POSITION;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            int2 _JumpFloodAxisWidth;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionCS = GetVertexPositionInputs(IN.positionOS.xyz).positionCS;
                return OUT;
            }

            float2 frag(Varyings IN) : SV_TARGET{
                //integer pixel pos
                int2 uvInt = int2(IN.positionCS.xy);

                //initialize best distance at infinity
                float bestDist = 1.#INF;
                float2 bestCoord;

                //jump samples
                UNITY_UNROLL
                for (int u = -1; u <= 1; u++) {
                    //calc offset sample pos
                    int2 offsetUV = uvInt + _JumpFloodAxisWidth * u;

                    //.Load() acts funny when sampling outside of bounds, so dont
                    offsetUV = clamp(offsetUV, int2(0, 0), (int2)_MainTex_TexelSize.zw - 1);

                    //decode position from buffer
                    float2 offsetPos = (_MainTex.Load(int3(offsetUV, 0)).rg + FLOOD_ENCODE_OFFSET) * _MainTex_TexelSize.zw / FLOOD_ENCODE_SCALE;

                    //the offset from current position
                    float2 disp = IN.positionCS.xy - offsetPos;

                    //square distance
                    float dist = dot(disp, disp);

                    //if offset position isn't a null position or is closer than the best
                    //set as the new best and store the position
                    if (offsetPos.x != -1.0 && dist < bestDist) {
                        bestDist = dist;
                        bestCoord = offsetPos;
                    }
                }

                //if not valid best distance output null position, otherwise output encoded position
                return isinf(bestDist) ? FLOOD_NULL_POS_FLOAT2 : bestCoord * _MainTex_TexelSize.xy * FLOOD_ENCODE_SCALE - FLOOD_ENCODE_OFFSET;
            }
            ENDHLSL
        }

        Pass{
            Name "JUMPFLOODOUTLINE"

            Stencil{
                Ref 1
                ReadMask 1
                WriteMask 1
                Comp NotEqual
                Pass Zero
                Fail Zero
            }

            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            struct Attributes {
                float4 positionOS : POSITION;
};

            struct Varyings {
                float4 positionCS : SV_POSITION;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            half4 _OutlineColor;
            float _OutlineWidth;

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionCS = GetVertexPositionInputs(IN.positionOS.xyz).positionCS;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_TARGET{
                //int pixel pos
                int2 uvInt = int2(IN.positionCS.xy);

                //load encoded position
                float2 encodedPos = _MainTex.Load(int3(uvInt, 0)).rg;

                //early out if null pos
                if (encodedPos.y == -1) {
                    return half4(0, 0, 0, 0);
                }

                //decode closest pos
                float2 nearestPos = (encodedPos + FLOOD_ENCODE_OFFSET) * abs(_ScreenParams.xy) / FLOOD_ENCODE_SCALE;

                //current pixel pos
                float2 currentPos = IN.positionCS.xy;

                //distance in pixels to closest pos
                half dist = length(nearestPos - currentPos);

                //calculate outline
                //+1.0 is because encoded nearest position is half a pixel inset
                //not + 0.5 because we want the anti-aliased edge to be aligned between pixels
                //distance is already in pixels so this is already perfectly anti-aliased!
                half outline = saturate(_OutlineWidth - dist + 1.0);

                //apply outline to alpha
                half4 col = _OutlineColor;
                col.a *= outline;

                //profit
                return col;
            }

            ENDHLSL
        }

        Pass{
            Name "Blit_To_Target"

            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            struct Attributes {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            Varyings vert(Attributes IN) {
                Varyings OUT;
                OUT.positionCS = GetVertexPositionInputs(IN.positionOS.xyz).positionCS;
                OUT.uv = TRANSFORM_TEX(IN.uv, _MainTex);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_TARGET{
                // sample the texture
                real4 col = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv);

                return col;
            }

        ENDHLSL
        }
    }
}
