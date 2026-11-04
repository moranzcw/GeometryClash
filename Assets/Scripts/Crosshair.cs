using UnityEngine;

namespace GeoClash
{
    /// <summary>
    /// 准星光标：程序化生成一张十字准星贴图，替换系统箭头光标。
    /// 走 Cursor.SetCursor 的硬件光标通道，所以没有"软件光标跟手延迟"的问题，
    /// 也不需要每帧更新。
    /// </summary>
    public static class Crosshair
    {
        static Texture2D tex;

        public static void Apply()
        {
            if (tex == null) tex = Make(48);
            if (tex == null) return;
            Cursor.SetCursor(tex, new Vector2(tex.width * 0.5f, tex.height * 0.5f), CursorMode.Auto);
        }

        // ---- 形状定义（尺寸以 48 像素为基准，按 size 缩放） ----

        /// <summary>距离场抗锯齿：v 在 half 以内返回 1，超出 feather 后衰减到 0。</summary>
        static float Band(float v, float half, float feather)
        {
            // 注意：Mathf.SmoothStep 是"在 from..to 之间插值"，不是着色器里的 smoothstep，
            // 直接拿来算遮罩会得到负值。这里手动做归一化 + 平滑阶跃。
            float t = Mathf.Clamp01((v - (half - feather)) / (2f * feather));
            return 1f - t * t * (3f - 2f * t);
        }

        /// <param name="d">到中心的距离（像素）</param>
        /// <param name="ax">|dx|</param>
        /// <param name="ay">|dy|</param>
        /// <param name="grow">向外扩张的量，用来画一圈暗边</param>
        static float Shape(float d, float ax, float ay, float grow, float k)
        {
            // 中心点
            float dot = Band(d, (2.2f + grow) * k, 0.9f * k);

            // 内圈
            float ring = Band(Mathf.Abs(d - 9f * k), (1.4f + grow) * k, 0.8f * k);

            // 四根刻线（十字），限制在半径 12~20 之间，中间留空不挡视线
            float perp = Mathf.Min(ax, ay);
            float inner = (12f - grow) * k;
            float outer = (20f + grow) * k;
            float gate = Mathf.Clamp01((d - inner) / (1.5f * k)) *
                         Mathf.Clamp01((outer - d) / (1.6f * k));
            float tick = Band(perp, (1.8f + grow) * k, 0.8f * k) * gate;

            return Mathf.Max(Mathf.Max(dot, ring), tick);
        }

        static Texture2D Make(int size)
        {
            float k = size / 48f;
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            t.name = "Crosshair";
            t.wrapMode = TextureWrapMode.Clamp;
            t.filterMode = FilterMode.Bilinear;

            var px = new Color32[size * size];
            float h = size * 0.5f;
            var bright = Color.white;

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - h;
                    float dy = y + 0.5f - h;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float ax = Mathf.Abs(dx);
                    float ay = Mathf.Abs(dy);

                    float core = Shape(d, ax, ay, 0f, k);
                    float wide = Shape(d, ax, ay, 1.4f, k);

                    // 亮线下面垫一圈半透明黑边，爆炸的白光里也能看清
                    float aDark = wide * 0.45f * (1f - core);
                    float aOut = core + aDark * (1f - core);

                    if (aOut <= 0.004f)
                    {
                        px[y * size + x] = new Color32(0, 0, 0, 0);
                        continue;
                    }

                    float inv = 1f / aOut;
                    px[y * size + x] = new Color32(
                        (byte)(Mathf.Clamp01(bright.r * core * inv) * 255f),
                        (byte)(Mathf.Clamp01(bright.g * core * inv) * 255f),
                        (byte)(Mathf.Clamp01(bright.b * core * inv) * 255f),
                        (byte)(Mathf.Clamp01(aOut) * 255f));
                }
            }

            t.SetPixels32(px);
            t.Apply();
            return t;
        }
    }
}
