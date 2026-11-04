// 弹簧网格的渲染着色器（共享顶点版）。
// 网格顶点就是质点本身，UV 直接存"格坐标"(c, r) —— 整数格的连续坐标。
// 大格线、小格线、大格顶点圆点全部由片元着色器按这个坐标算出来，
// 因为坐标是全局的，图案天然跨格连续，和模拟分辨率无关。
Shader "GeoClash/GridCell"
{
    Properties
    {
        _LineColor   ("Major Line Color", Color) = (0.15, 0.43, 1.05, 1)
        _SubColor    ("Sub Line Color", Color) = (0.105, 0.28, 0.70, 1)
        _SubStrength ("Sub Line Strength", Float) = 0.62
        _LineWidth   ("Line Width (px)", Float) = 1.1
        _MajorEvery  ("Sim Cells Per Major Cell", Float) = 5
        _SubCells    ("Sub Cells Per Major Cell", Float) = 4
        _DotBoost    ("Vertex Dot Boost", Float) = 1.8
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
            "RenderType" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "GridCell"
            Tags { "LightMode" = "UniversalForward" }

            Blend SrcAlpha One
            ZWrite Off
            ZTest Always
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _LineColor;
                half4 _SubColor;
                float _SubStrength;
                float _LineWidth;
                float _MajorEvery;
                float _SubCells;
                float _DotBoost;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 cell       : TEXCOORD0;   // 全局格坐标 (c, r)
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 cell       : TEXCOORD0;
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                OUT.cell = IN.cell;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 c = IN.cell;
                float2 fw = max(fwidth(c), 1e-6);

                // 小格线：每 (大格模拟格数 / 每个大格里的小格数) 个模拟格一条。
                // 小格间距和模拟格距是**解耦**的 —— 模拟格距 Cell 一改就要连带重调弹簧刚度，
                // 而这里只想要"每个大格里 4×4 条线"的视觉效果，所以单独用一个周期来控制。
                // 例：_MajorEvery=5、_SubCells=4 → 每 1.25 个模拟格（= 0.35 世界单位）一条。
                float subPeriod = _MajorEvery / max(_SubCells, 1.0);
                float2 cs = c / subPeriod;
                float2 fws = fw / subPeriod;
                float2 dl = abs(frac(cs + 0.5) - 0.5) / fws;
                float sub = 1.0 - saturate(min(dl.x, dl.y) / _LineWidth);

                // 大格线：每 _MajorEvery 个模拟格一条（和以前完全一致）
                float2 g = c / _MajorEvery;
                float2 dg = abs(frac(g + 0.5) - 0.5) * _MajorEvery / fw;
                float major = 1.0 - saturate(min(dg.x, dg.y) / (_LineWidth * 1.4));

                // 大格顶点圆点：到最近一个大格顶点的距离
                float2 gc = round(g) * _MajorEvery;
                float fwAvg = max(0.5 * (fw.x + fw.y), 1e-6);
                float dotPx = length(c - gc) / fwAvg;
                float dotR = _LineWidth * 1.2;                 // 直径 = 1.2 倍线宽
                float dotCore = 1.0 - saturate(dotPx / dotR);
                float dotHalo = 1.0 - saturate(dotPx / (dotR * 2.2));
                dotHalo *= dotHalo;

                float3 col = _LineColor.rgb * major;
                col += _SubColor.rgb * sub * _SubStrength;
                col += _LineColor.rgb * _DotBoost * dotCore;
                col += _LineColor.rgb * 0.45 * dotHalo;

                return half4(col, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
