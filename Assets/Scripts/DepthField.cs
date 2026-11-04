using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoClash
{
    /// <summary>
    /// 场地后方的 3D 点云（泊松盘分布）。
    ///
    /// 游戏本身是 z=0 上的二维平面，相机只在平面正前方平移（SwayX / SwayY）并轻微推拉。
    /// 在平面背后撒一层有厚度的点之后，相机一平移，近处和远处的点在屏幕上走过的
    /// 距离不一样（视差），画面立刻就有了"立在一块三维空间里"的体积感。
    ///
    /// 为什么是泊松盘而不是等距点阵：
    ///  - 等距点阵在透视下会形成规则网格/摩尔纹，一眼就看得出是块背景板；
    ///  - 纯随机撒点又会结块，出现一片密一片空。
    /// 泊松盘（Bridson 算法）保证**任意两点距离不小于 MinDist**，位置本身是随机的，
    /// 也就是所谓的"蓝噪声"分布 —— 疏密均匀但没有规律性。
    ///
    /// 几个刻意的取舍：
    ///  - 全部落在 z > 0（场地之后），最近留 3 个单位，不会挡在战斗画面前面。
    ///  - XY 径向淡出（r&lt;0.62 满亮，0.62→1.0 收到 0），所以点云没有硬边界。
    ///  - 尺寸和亮度都随深度**连续**变化，不再有离散的分层感。
    /// </summary>
    public class DepthField : MonoBehaviour
    {
        public static DepthField I;

        // ------------------------------------------------------------ 参数

        /// <summary>泊松盘最小间距（三维空间里的欧氏距离）。越大越稀 —— 这是调整疏密的主旋钮。</summary>
        const float MinDist = 6f;

        /// <summary>XY 覆盖范围：比场地（±41.3 / ±24.5）大一圈，外面靠淡出收掉。</summary>
        const float HalfX = 78.8f;
        const float HalfY = 62f;

        /// <summary>深度范围：全部在场地之后。</summary>
        const float ZNear = 3f;
        const float ZFar = 42f;

        /// <summary>点的世界尺寸（近 / 远）。远处的点会被透视缩小，这里补一点回来。</summary>
        const float SizeNear = 0.40f;
        const float SizeFar = 0.70f;

        /// <summary>亮度（近 / 远）。叠加混合下最终亮度 = Tint × alpha。</summary>
        const float AlphaNear = 0.86f;
        const float AlphaFar = 0.27f;

        /// <summary>点的颜色：比网格线更暗更偏蓝，读作背景。</summary>
        static readonly Color Tint = new Color(0.22f, 0.42f, 0.70f, 1f);

        /// <summary>固定种子：同一份参数每次生成的点云完全一致，方便反复对比调参。</summary>
        const int Seed = 20261104;

        // ------------------------------------------------------------ 构建

        void Awake()
        {
            I = this;
            Build();
        }

        void Build()
        {
            var pts = SamplePoisson();

            int cap = pts.Count;
            var verts = new Vector3[cap * 4];
            var uvs = new Vector2[cap * 4];
            var vcols = new Color[cap * 4];
            var tris = new int[cap * 6];

            int vi = 0, ti = 0;

            for (int i = 0; i < cap; i++)
            {
                Vector3 p = pts[i];

                // XY 径向淡出：r < 0.62 满亮，0.62→1.0 平滑收到 0，之外不要。
                float rx = p.x / HalfX, ry = p.y / HalfY;
                float r = Mathf.Sqrt(rx * rx + ry * ry);
                if (r >= 1f) continue;

                float edge = Mathf.Clamp01((1f - r) / 0.38f);
                edge = edge * edge * (3f - 2f * edge);           // smoothstep
                if (edge < 0.01f) continue;

                // 尺寸和亮度随深度连续变化
                float t = Mathf.InverseLerp(ZNear, ZFar, p.z);
                float size = Mathf.Lerp(SizeNear, SizeFar, t);
                float alpha = Mathf.Lerp(AlphaNear, AlphaFar, t);

                Color c = Tint;
                c.a = alpha * edge;

                float h = size * 0.5f;
                verts[vi + 0] = new Vector3(p.x - h, p.y - h, p.z);
                verts[vi + 1] = new Vector3(p.x - h, p.y + h, p.z);
                verts[vi + 2] = new Vector3(p.x + h, p.y + h, p.z);
                verts[vi + 3] = new Vector3(p.x + h, p.y - h, p.z);

                uvs[vi + 0] = new Vector2(0f, 0f);
                uvs[vi + 1] = new Vector2(0f, 1f);
                uvs[vi + 2] = new Vector2(1f, 1f);
                uvs[vi + 3] = new Vector2(1f, 0f);

                vcols[vi + 0] = c; vcols[vi + 1] = c;
                vcols[vi + 2] = c; vcols[vi + 3] = c;

                tris[ti + 0] = vi; tris[ti + 1] = vi + 1; tris[ti + 2] = vi + 2;
                tris[ti + 3] = vi; tris[ti + 4] = vi + 2; tris[ti + 5] = vi + 3;

                vi += 4;
                ti += 6;
            }

            // 裁掉没用的尾巴，否则剩余那些默认值 (0,0,0) 会在原点堆成一块
            Array.Resize(ref verts, vi);
            Array.Resize(ref uvs, vi);
            Array.Resize(ref vcols, vi);
            Array.Resize(ref tris, ti);

            var mesh = new Mesh { name = "DepthField" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(vcols);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(
                new Vector3(0f, 0f, (ZNear + ZFar) * 0.5f),
                new Vector3(HalfX * 2f + 4f, HalfY * 2f + 4f, ZFar - ZNear + 4f));

            // 柔和辉光材质：每个四边形会被化成边缘自然消隐的圆点，不会看到方角
            MatLib.Attach(gameObject, mesh, MatLib.Glow, "DepthField");

            Debug.Log(string.Format("[GeoClash] 深度点云：采样 {0} 点，实际绘制 {1} 点（泊松最小间距 {2}）",
                cap, vi / 4, MinDist));
        }

        /// <summary>
        /// Bridson 泊松盘采样（三维）。
        ///
        /// 维护一个"活跃点"列表，每次随机挑一个活跃点，在它周围 [MinDist, 2·MinDist]
        /// 的球壳里试若干候选位置；只要候选点与所有已有点的距离都不小于 MinDist 就收下，
        /// 并把它也加入活跃列表。一个点试满次数都失败，说明它周围已经饱和，从活跃列表移除。
        ///
        /// 加速：用边长 MinDist/√3 的均匀网格做空间索引 —— 立方体对角线正好等于 MinDist，
        /// 所以每格最多一个点，判断候选点时只查 3×3×3 邻域。
        /// </summary>
        List<Vector3> SamplePoisson()
        {
            // 格子边长取 MinDist/√3 的 0.95 倍，而不是正好 /√3：
            // 正好 /√3 时立方体对角线**恰好**等于 MinDist，两个相距恰好 MinDist 的点有可能落进同一格，
            // 而这里一格只存一个索引，后写进去的会把前一个覆盖掉、从此查不到。
            // 缩到 0.95 倍后对角线 = 0.95·MinDist，严格小于 MinDist，"一格最多一个点"才真正成立。
            float cell = MinDist * 0.95f / Mathf.Sqrt(3f);
            int gx = Mathf.CeilToInt(HalfX * 2f / cell) + 1;
            int gy = Mathf.CeilToInt(HalfY * 2f / cell) + 1;
            int gz = Mathf.CeilToInt((ZFar - ZNear) / cell) + 1;

            var grid = new int[gx * gy * gz];
            for (int i = 0; i < grid.Length; i++) grid[i] = -1;

            var rng = new System.Random(Seed);
            var pts = new List<Vector3>(4096);
            var active = new List<int>(4096);

            int IX(float v) { return Mathf.Clamp(Mathf.FloorToInt((v + HalfX) / cell), 0, gx - 1); }
            int IY(float v) { return Mathf.Clamp(Mathf.FloorToInt((v + HalfY) / cell), 0, gy - 1); }
            int IZ(float v) { return Mathf.Clamp(Mathf.FloorToInt((v - ZNear) / cell), 0, gz - 1); }
            int Idx(int x, int y, int z) { return (z * gy + y) * gx + x; }

            bool Fits(Vector3 c)
            {
                int cx = IX(c.x), cy = IY(c.y), cz = IZ(c.z);
                float d2 = MinDist * MinDist;

                // 邻域必须查到 ±2 格，不能只查 ±1：
                // 格子边长是 MinDist/√3，两个间距 < MinDist 的点，格索引最多能差
                // MinDist/cell + 1 = √3 + 1 ≈ 2.73，也就是 2 格。只查 ±1 会漏掉一部分冲突，
                // 实测那样做最小间距会掉到 4.4 左右（本来该 ≥ 7）。
                int z0 = Mathf.Max(0, cz - 2), z1 = Mathf.Min(gz - 1, cz + 2);
                int y0 = Mathf.Max(0, cy - 2), y1 = Mathf.Min(gy - 1, cy + 2);
                int x0 = Mathf.Max(0, cx - 2), x1 = Mathf.Min(gx - 1, cx + 2);

                for (int z = z0; z <= z1; z++)
                {
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            int id = grid[Idx(x, y, z)];
                            if (id < 0) continue;
                            if ((pts[id] - c).sqrMagnitude < d2) return false;
                        }
                    }
                }
                return true;
            }

            void Add(Vector3 p)
            {
                int id = pts.Count;
                pts.Add(p);
                grid[Idx(IX(p.x), IY(p.y), IZ(p.z))] = id;
                active.Add(id);
            }

            // 种子点：场地中心、深度正中，让点云从这里均匀铺开
            Add(new Vector3(0f, 0f, (ZNear + ZFar) * 0.5f));

            const int Attempts = 30;
            while (active.Count > 0)
            {
                int ai = rng.Next(active.Count);
                Vector3 p = pts[active[ai]];
                bool placed = false;

                for (int k = 0; k < Attempts; k++)
                {
                    // 球面上均匀取一个方向，半径落在 [MinDist, 2·MinDist]
                    float u = (float)rng.NextDouble() * 2f - 1f;
                    float th = (float)rng.NextDouble() * Mathf.PI * 2f;
                    float s = Mathf.Sqrt(Mathf.Max(0f, 1f - u * u));
                    Vector3 dir = new Vector3(s * Mathf.Cos(th), s * Mathf.Sin(th), u);

                    Vector3 c = p + dir * (MinDist * (1f + (float)rng.NextDouble()));

                    if (c.x < -HalfX || c.x > HalfX) continue;
                    if (c.y < -HalfY || c.y > HalfY) continue;
                    if (c.z < ZNear || c.z > ZFar) continue;
                    if (!Fits(c)) continue;

                    Add(c);
                    placed = true;
                    break;
                }

                // 试满都不行：这个点再也生不出新点，从活跃列表移除（交换删除，O(1)）
                if (!placed)
                {
                    active[ai] = active[active.Count - 1];
                    active.RemoveAt(active.Count - 1);
                }
            }

            return pts;
        }
    }
}
