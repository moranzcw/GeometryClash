using System.Collections.Generic;
using UnityEngine;

namespace GeoClash
{
    /// <summary>材质缓存。同色材质共享，避免每个物体新建材质。</summary>
    public static class MatLib
    {
        static Shader neonShader;
        static Shader gridShader;
        static readonly Dictionary<int, Material> cache = new Dictionary<int, Material>();
        static Material particleMat;
        static Material glowMat;
        static Texture2D glow;
        static Material gridMat;
        static Material enemyMat;
        static readonly Material[] enemyFlashMats = new Material[4];
        static readonly Dictionary<long, Material> scaledCache = new Dictionary<long, Material>();

        /// <summary>
        /// 返回一个 _Color 已经乘过 mul 的**共享**材质，用来做"淡出 / 闪烁"这类按物体改亮度的效果。
        ///
        /// 为什么这等价于改 alpha：着色器是 `SrcAlpha One`，最终亮度 = rgb × alpha。
        /// alpha 恒为 1 时把 rgb 乘 mul，和"rgb 不变、alpha 乘 mul"的结果一模一样，
        /// 但这样同一个 mul 的所有物体可以**共用一份材质**，从而保留 SRP Batcher 合批；
        /// 用 MaterialPropertyBlock 则会直接把物体踢出合批。
        ///
        /// mul 量化到 1/32 档，既够平滑（32 级渐变肉眼看不出台阶），又不会让缓存无限膨胀。
        /// </summary>
        public static Material NeonScaled(Color c, float mul)
        {
            int step = Mathf.Clamp(Mathf.RoundToInt(mul * 32f), 0, 32);
            if (step == 32) return Neon(c);

            long key = ScaledKey(c, step);
            Material m;
            if (scaledCache.TryGetValue(key, out m) && m != null) return m;

            if (NeonShader == null) return null;

            float k = step / 32f;
            m = new Material(NeonShader);
            m.name = "NeonS_" + key;
            m.SetColor("_Color", new Color(c.r * k, c.g * k, c.b * k, 1f));
            scaledCache[key] = m;
            return m;
        }

        /// <summary>颜色量化到 1/256 + 档位打包成一个 long，避免哈希碰撞导致颜色串味。</summary>
        static long ScaledKey(Color c, int step)
        {
            long r = Mathf.RoundToInt(Mathf.Clamp(c.r, 0f, 3.9f) * 256f);
            long g = Mathf.RoundToInt(Mathf.Clamp(c.g, 0f, 3.9f) * 256f);
            long b = Mathf.RoundToInt(Mathf.Clamp(c.b, 0f, 3.9f) * 256f);
            return (r << 40) | (g << 28) | (b << 16) | (long)step;
        }

        /// <summary>
        /// 敌人共享材质用的 HDR 增益。
        ///
        /// 敌人颜色是烘进顶点色的，而顶点色会被 Unity 钳到 0~1（SetColors 内部转 Color32），
        /// 直接写 (1.75,0.42,2.40) 会被截成 (1,0.42,1)，辉光和色相都丢了。
        /// 所以顶点色里只存**归一化色相** c/max(c)，亮度用 alpha 带 max(c)/Gain，
        /// 再由材质的 _Color 乘回 Gain —— 叠加混合下 rgb×alpha 正好还原成原始颜色 c。
        ///
        /// 取 2.7 是因为所有敌人配色里最大通道是 Dodger 的 2.70。
        /// </summary>
        public const float EnemyGain = 2.7f;

        /// <summary>
        /// 全部敌人共用的材质（_Color 只给 HDR 增益，颜色在顶点色里）。
        ///
        /// 关键点：**不再用 MaterialPropertyBlock**。MPB 会让物体被排除在 SRP Batcher 之外，
        /// 几百个敌人就是几百次 SetPass；共用同一个材质之后它们能合进同一批。
        /// </summary>
        public static Material EnemyShared
        {
            get
            {
                if (enemyMat == null && NeonShader != null)
                {
                    enemyMat = new Material(NeonShader);
                    enemyMat.name = "EnemyShared";
                    enemyMat.SetColor("_Color", new Color(EnemyGain, EnemyGain, EnemyGain, 1f));
                }
                return enemyMat;
            }
        }

        /// <summary>
        /// 受击闪白用的共享材质（step 1~3，越亮越白）。
        ///
        /// 同样走共享材质而不是 MPB：闪烁时只是把 renderer.sharedMaterial 换一下，
        /// 依然是同一个着色器，SRP Batcher 照样能合批。
        /// 因为最终是叠加混合，单纯把增益调高就会让各个通道一起溢出到 1，看起来就是"闪白"。
        /// </summary>
        public static Material EnemyFlash(int step)
        {
            step = Mathf.Clamp(step, 1, 3);
            if (enemyFlashMats[step] == null && NeonShader != null)
            {
                float gain = EnemyGain * (1f + step * 1.8f);   // 2.7 → 4.86 / 7.02 / 9.18
                var m = new Material(NeonShader);
                m.name = "EnemyFlash" + step;
                m.SetColor("_Color", new Color(gain, gain, gain, 1f));
                enemyFlashMats[step] = m;
            }
            return enemyFlashMats[step];
        }

        public static Shader NeonShader
        {
            get
            {
                if (neonShader == null)
                {
                    // 必须走 Resources：打包时未被任何资源引用的 shader 会被剥离，
                    // 只靠 Shader.Find 在编辑器里能拿到、打包后就返回 null。
                    neonShader = Resources.Load<Shader>("Neon");
                    if (neonShader == null) neonShader = Shader.Find("GeoClash/Neon");
                    if (neonShader == null) Debug.LogError("[GeoClash] 找不到着色器 Neon（应为 Resources/Neon.shader）");
                }
                return neonShader;
            }
        }

        public static void Reset()
        {
            cache.Clear();
            particleMat = null;
            glowMat = null;
            glow = null;
            gridMat = null;
            neonShader = null;
            gridShader = null;
            enemyMat = null;
            for (int i = 0; i < enemyFlashMats.Length; i++) enemyFlashMats[i] = null;
            scaledCache.Clear();
        }

        public static Material Neon(Color c)
        {
            int key = c.GetHashCode();
            Material m;
            if (cache.TryGetValue(key, out m) && m != null) return m;

            if (NeonShader == null) return null;

            m = new Material(NeonShader);
            m.name = "Neon_" + key;
            m.SetColor("_Color", c);
            m.enableInstancing = false;
            cache[key] = m;
            return m;
        }

        /// <summary>
        /// 硬边粒子材质：不带贴图（默认纯白），所以每个四边形都是实心纯色块，
        /// 四边是硬的、没有柔和渐变收边。颜色完全由顶点色决定。
        /// </summary>
        public static Material Particle
        {
            get
            {
                if (particleMat == null && NeonShader != null)
                {
                    particleMat = new Material(NeonShader);
                    particleMat.name = "Particle";
                    particleMat.SetColor("_Color", Color.white);
                }
                return particleMat;
            }
        }

        /// <summary>
        /// 柔和粒子材质：带一张径向辉光贴图，四边形会被化成一个边缘自然消隐的圆团。
        /// 用于白闪、爆心这类需要"软"的效果，和硬边矩形形成对比。
        /// </summary>
        public static Material Glow
        {
            get
            {
                if (glowMat == null && NeonShader != null)
                {
                    glowMat = new Material(NeonShader);
                    glowMat.name = "Glow";
                    glowMat.SetColor("_Color", Color.white);
                    glowMat.mainTexture = GlowTex;
                }
                return glowMat;
            }
        }

        public static Texture2D GlowTex
        {
            get
            {
                if (glow == null) glow = MakeGlow(64);
                return glow;
            }
        }

        /// <summary>
        /// 径向辉光：中心不透明，向外平滑衰减到 0。
        /// 衰减用 smoothstep 之后再平方一次 —— 中心能保持实、边缘才化得开，不会糊成一团灰。
        /// </summary>
        static Texture2D MakeGlow(int size)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            t.name = "Glow";
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;
            var px = new Color32[size * size];

            for (int y = 0; y < size; y++)
            {
                float v = ((y + 0.5f) / size) * 2f - 1f;
                for (int x = 0; x < size; x++)
                {
                    float u = ((x + 0.5f) / size) * 2f - 1f;

                    // r = 到贴图中心的归一化距离（0 = 中心，1 = 边缘）
                    float r = Mathf.Sqrt(u * u + v * v);
                    float a = Mathf.Clamp01(1f - r);
                    a = a * a * (3f - 2f * a);   // smoothstep：中段过渡更软
                    a *= a;                      // 再压一次，边缘彻底化开

                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }

            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        /// <summary>弹簧网格单元的材质（大格边框 + 5×5 小格线，全部由着色器按 UV 画出）。</summary>
        public static Material GridCell
        {
            get
            {
                if (gridMat == null && GridShader != null)
                {
                    gridMat = new Material(GridShader);
                    gridMat.name = "GridCell";
                    gridMat.SetColor("_LineColor", Palette.GridMajor);
                    gridMat.SetColor("_SubColor", Palette.GridMinor);
                    gridMat.SetFloat("_SubStrength", 0.62f);
                    gridMat.SetFloat("_LineWidth", 1.1f);
                    gridMat.SetFloat("_MajorEvery", SpringGrid.MajorEvery);
                    gridMat.SetFloat("_SubCells", SpringGrid.SubCells);
                    gridMat.SetFloat("_DotBoost", 1.8f);
                }
                return gridMat;
            }
        }

        public static Shader GridShader
        {
            get
            {
                if (gridShader == null)
                {
                    gridShader = Resources.Load<Shader>("GridCell");
                    if (gridShader == null) gridShader = Shader.Find("GeoClash/GridCell");
                    if (gridShader == null) Debug.LogError("[GeoClash] 找不到着色器 GridCell（应为 Resources/GridCell.shader）");
                }
                return gridShader;
            }
        }
        /// <summary>给物体挂一个网格渲染器。</summary>
        public static MeshRenderer Attach(GameObject go, Mesh mesh, Material mat, string name = null)
        {
            if (name != null) go.name = name;
            var mf = go.GetComponent<MeshFilter>();
            if (mf == null) mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = mesh;
            var mr = go.GetComponent<MeshRenderer>();
            if (mr == null) mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            return mr;
        }
    }
}
