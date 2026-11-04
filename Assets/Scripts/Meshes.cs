using System;
using System.Collections.Generic;
using UnityEngine;

namespace GeoClash
{
    /// <summary>
    /// 在 XY 平面上拼网格的小工具。整个游戏平面是"立起来"的（z=0），
    /// 摄像机从 -Z 方向看向它。
    /// </summary>
    public class MeshBuilder
    {
        readonly List<Vector3> verts = new List<Vector3>();
        readonly List<Vector2> uvs = new List<Vector2>();
        readonly List<Color> cols = new List<Color>();
        readonly List<int> tris = new List<int>();

        public void AddQuad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col)
        {
            int i = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c); verts.Add(d);
            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(1f, 1f));
            uvs.Add(new Vector2(0f, 1f));
            cols.Add(col); cols.Add(col); cols.Add(col); cols.Add(col);
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
            tris.Add(i); tris.Add(i + 2); tris.Add(i + 3);
        }

        public void AddTri(Vector3 a, Vector3 b, Vector3 c, Color col)
        {
            int i = verts.Count;
            verts.Add(a); verts.Add(b); verts.Add(c);
            uvs.Add(new Vector2(0f, 0f));
            uvs.Add(new Vector2(1f, 0f));
            uvs.Add(new Vector2(0.5f, 1f));
            cols.Add(col); cols.Add(col); cols.Add(col);
            tris.Add(i); tris.Add(i + 1); tris.Add(i + 2);
        }

        /// <summary>在 XY 平面上画一条有宽度的线段（两端各延长半个线宽，弥补拐角缺口）。</summary>
        public void AddLine(Vector3 a, Vector3 b, float width, Color col)
        {
            Vector3 dir = new Vector3(b.x - a.x, b.y - a.y, 0f);
            float len = dir.magnitude;
            if (len < 1e-5f) return;
            dir /= len;
            Vector3 n = new Vector3(-dir.y, dir.x, 0f) * (width * 0.5f);
            Vector3 ext = dir * (width * 0.5f);
            Vector3 a0 = a - ext;
            Vector3 b0 = b + ext;
            AddQuad(a0 - n, b0 - n, b0 + n, a0 + n, col);
        }

        /// <summary>从中心点向多边形各顶点做三角扇，用作内部微光填充。</summary>
        public void AddFan(Vector3[] pts, Color col)
        {
            Vector3 c = Vector3.zero;
            for (int i = 0; i < pts.Length; i++) c += pts[i];
            c /= pts.Length;
            for (int i = 0; i < pts.Length; i++)
            {
                AddTri(c, pts[i], pts[(i + 1) % pts.Length], col);
            }
        }

        public Mesh Build(string name)
        {
            var m = new Mesh { name = name };
            m.SetVertices(verts);
            m.SetUVs(0, uvs);
            m.SetColors(cols);
            m.SetTriangles(tris, 0);
            m.RecalculateBounds();
            return m;
        }
    }

    public static class Meshes
    {
        static readonly Dictionary<string, Mesh> cache = new Dictionary<string, Mesh>();

        static Mesh Cached(string key, Func<Mesh> factory)
        {
            Mesh m;
            if (cache.TryGetValue(key, out m) && m != null) return m;
            m = factory();
            cache[key] = m;
            return m;
        }

        public static void ClearCache() { cache.Clear(); }

        static Vector3[] RingPoints(int sides, float radius, float rotDeg)
        {
            var pts = new Vector3[sides];
            float rot = rotDeg * Mathf.Deg2Rad;
            for (int i = 0; i < sides; i++)
            {
                float a = rot + i * Mathf.PI * 2f / sides;
                pts[i] = new Vector3(Mathf.Cos(a) * radius, Mathf.Sin(a) * radius, 0f);
            }
            return pts;
        }

        /// <summary>正多边形：内部填充 + 发光轮廓线。</summary>
        public static Mesh Poly(int sides, float radius, float thickness, float fillAlpha = 0.10f, float rotDeg = 0f)
        {
            string key = string.Format("poly_{0}_{1:0.###}_{2:0.###}_{3:0.###}_{4:0.###}", sides, radius, thickness, fillAlpha, rotDeg);
            return Cached(key, () =>
            {
                var mb = new MeshBuilder();
                var pts = RingPoints(sides, radius, rotDeg);
                if (fillAlpha > 0.001f) mb.AddFan(pts, new Color(1f, 1f, 1f, fillAlpha));
                for (int i = 0; i < pts.Length; i++) mb.AddLine(pts[i], pts[(i + 1) % pts.Length], thickness, Color.white);
                return mb.Build(key);
            });
        }

        /// <summary>空心圆环（用于黑洞 / 冲击波 / 子弹）。</summary>
        public static Mesh Ring(int segs, float radius, float thickness)
        {
            string key = string.Format("ring_{0}_{1:0.###}_{2:0.###}", segs, radius, thickness);
            return Cached(key, () =>
            {
                var mb = new MeshBuilder();
                var pts = RingPoints(segs, radius, 0f);
                for (int i = 0; i < pts.Length; i++) mb.AddLine(pts[i], pts[(i + 1) % pts.Length], thickness, Color.white);
                return mb.Build(key);
            });
        }

        /// <summary>
        /// 玩家飞船：纯轮廓的盾形棱角几何体，前缘微微内凹（没有机头，不像飞机）。
        /// 朝向 +Y（屏幕上方）。多边形相对质心是星形的，可直接三角扇填充。
        /// </summary>
        public static Mesh Ship()
        {
            return Cached("ship6", () =>
            {
                var mb = new MeshBuilder();

                var hull = new[]
                {
                    new Vector3(-0.42f, 0.95f, 0f),   // 前缘左
                    new Vector3(0f, 0.78f, 0f),       // 前缘中点内凹
                    new Vector3(0.42f, 0.95f, 0f),    // 前缘右
                    new Vector3(1.05f, 0.10f, 0f),    // 右肩
                    new Vector3(0.62f, -0.80f, 0f),   // 右后角
                    new Vector3(0f, -0.55f, 0f),      // 尾部凹口
                    new Vector3(-0.62f, -0.80f, 0f),  // 左后角
                    new Vector3(-1.05f, 0.10f, 0f),   // 左肩
                };

                // 机身填充压得很淡，整体读作"线框"而不是实心块
                mb.AddFan(hull, new Color(1f, 1f, 1f, 0.05f));

                for (int i = 0; i < hull.Length; i++)
                {
                    mb.AddLine(hull[i], hull[(i + 1) % hull.Length], 0.105f, Color.white);
                }

                return mb.Build("ship6");
            });
        }

        /// <summary>矩形（子弹）。</summary>
        public static Mesh Rect(float w, float h)
        {
            string key = string.Format("rect_{0:0.###}_{1:0.###}", w, h);
            return Cached(key, () =>
            {
                var mb = new MeshBuilder();
                float x = w * 0.5f, y = h * 0.5f;
                mb.AddQuad(new Vector3(-x, -y, 0f), new Vector3(-x, y, 0f), new Vector3(x, y, 0f), new Vector3(x, -y, 0f), Color.white);
                return mb.Build(key);
            });
        }

        /// <summary>竞技场边框。</summary>
        public static Mesh Border(float halfX, float halfY, float thickness)
        {
            string key = string.Format("border_{0:0.##}_{1:0.##}_{2:0.##}", halfX, halfY, thickness);
            return Cached(key, () =>
            {
                var mb = new MeshBuilder();
                var a = new Vector3(-halfX, -halfY, 0f);
                var b = new Vector3(-halfX, halfY, 0f);
                var c = new Vector3(halfX, halfY, 0f);
                var d = new Vector3(halfX, -halfY, 0f);
                mb.AddLine(a, b, thickness, Color.white);
                mb.AddLine(b, c, thickness, Color.white);
                mb.AddLine(c, d, thickness, Color.white);
                mb.AddLine(d, a, thickness, Color.white);
                return mb.Build(key);
            });
        }
    }
}
