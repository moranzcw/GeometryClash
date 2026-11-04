using UnityEngine;

namespace GeoClash
{
    /// <summary>
    /// 敌人死亡时的"分裂"效果：把敌人的轮廓拆成一条条边，让它们向外慢慢飘散、
    /// 边飘边转、1~2 个大格的距离后淡出消失。
    ///
    /// 所有碎片画在一个动态 Mesh 里（每个碎片一个四边形，朝向由碎片自己决定，
    /// 不跟随速度），所以不产生任何 GameObject。
    /// </summary>
    public class SplitFx : MonoBehaviour
    {
        public static SplitFx I;

        const int MAX = 640;
        static readonly Vector3 Hidden = new Vector3(0f, -999f, 0f);

        /// <summary>
        /// 顶点色的 HDR 增益。
        /// Unity 的 Mesh.SetColors 会把顶点色钳到 0~1（内部转 Color32），
        /// 而敌人颜色是 HDR（如 1.75/0.42/2.40），直接写进去会被压成 1/0.42/1 —— 亮度掉到 Bloom
        /// 阈值附近，看起来就没有辉光。所以顶点色只存"归一化后的色相"，亮度靠材质 _Color 的增益
        /// 和顶点 alpha 还原（叠加混合下最终亮度 = rgb × alpha）。
        /// </summary>
        const float ColorGain = 2.2f;

        struct Piece
        {
            public Vector2 pos;
            public Vector2 vel;
            public float ang;      // 当前朝向（弧度）
            public float spin;     // 角速度（弧度/秒）
            public float len;      // 线段长度（含两端各半个线宽的延长）
            public float wid;      // 线宽
            public Color col;      // 归一化色相，alpha 里带着原始亮度
            public float life, maxLife;
        }

        Piece[] ps = new Piece[MAX];
        int count;
        int lastDrawn = -1;

        Mesh mesh;
        Vector3[] verts;
        Color[] cols;
        int[] tris;
        Material mat;

        void Awake()
        {
            I = this;

            verts = new Vector3[MAX * 4];
            cols = new Color[MAX * 4];
            tris = new int[MAX * 6];
            var uvs = new Vector2[MAX * 4];

            for (int i = 0; i < MAX; i++)
            {
                int v = i * 4, t = i * 6;
                tris[t + 0] = v; tris[t + 1] = v + 1; tris[t + 2] = v + 2;
                tris[t + 3] = v; tris[t + 4] = v + 2; tris[t + 5] = v + 3;

                uvs[v + 0] = new Vector2(0f, 0f);
                uvs[v + 1] = new Vector2(0f, 1f);
                uvs[v + 2] = new Vector2(1f, 1f);
                uvs[v + 3] = new Vector2(1f, 0f);
            }
            for (int i = 0; i < verts.Length; i++) verts[i] = Hidden;

            mesh = new Mesh { name = "SplitMesh" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.MarkDynamic();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetColors(cols);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(400f, 400f, 400f));

            // 专用材质：不能用 MatLib.Particle（_Color 是纯白），否则顶点色再亮也过不了 Bloom 阈值
            mat = new Material(MatLib.NeonShader);
            mat.name = "SplitPiece";
            mat.SetColor("_Color", new Color(ColorGain, ColorGain, ColorGain, 1f));

            MatLib.Attach(gameObject, mesh, mat);
        }

        /// <summary>
        /// 按敌人的轮廓把它拆成碎片。
        /// center = 敌人世界位置，angleDeg = 本体朝向，size = 体型倍率。
        /// </summary>
        public void Split(Vector3 center, float angleDeg, float size, Color c, Enemy.ShapeDef[] shapes)
        {
            if (shapes == null) return;

            float body = angleDeg * Mathf.Deg2Rad;
            float bc = Mathf.Cos(body), bs = Mathf.Sin(body);

            // 把 HDR 颜色拆成"归一化色相 + 亮度"：色相进顶点色（0~1 不会被钳），亮度进 alpha
            float mx = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (mx < 1e-4f) mx = 1f;
            Color norm = new Color(c.r / mx, c.g / mx, c.b / mx, Mathf.Clamp01(mx / ColorGain));

            for (int si = 0; si < shapes.Length; si++)
            {
                var s = shapes[si];
                int sides = Mathf.Max(3, s.sides);
                float rot = s.rotDeg * Mathf.Deg2Rad;
                float thick = s.thickness * size;

                for (int i = 0; i < sides; i++)
                {
                    if (count >= MAX) return;

                    float a0 = rot + i * Mathf.PI * 2f / sides;
                    float a1 = rot + (i + 1) * Mathf.PI * 2f / sides;

                    // 局部点 → 乘体型 → 乘本体朝向 → 平移到世界
                    float x0 = Mathf.Cos(a0) * s.radius * size, y0 = Mathf.Sin(a0) * s.radius * size;
                    float x1 = Mathf.Cos(a1) * s.radius * size, y1 = Mathf.Sin(a1) * s.radius * size;

                    float wx0 = center.x + x0 * bc - y0 * bs;
                    float wy0 = center.y + x0 * bs + y0 * bc;
                    float wx1 = center.x + x1 * bc - y1 * bs;
                    float wy1 = center.y + x1 * bs + y1 * bc;

                    float dx = wx1 - wx0, dy = wy1 - wy0;
                    float len = Mathf.Sqrt(dx * dx + dy * dy);
                    if (len < 1e-4f) continue;

                    float dirx = dx / len, diry = dy / len;
                    Vector2 mid = new Vector2((wx0 + wx1) * 0.5f, (wy0 + wy1) * 0.5f);

                    // 向外飘：从敌人中心指向这条边中点
                    Vector2 outDir = mid - new Vector2(center.x, center.y);
                    if (outDir.sqrMagnitude < 1e-5f) outDir = new Vector2(dirx, diry);
                    outDir.Normalize();

                    float spd = Random.Range(1.8f, 2.8f);
                    Vector2 vel = outDir * spd + new Vector2(-diry, dirx) * Random.Range(-0.25f, 0.25f);

                    var p = new Piece();
                    p.pos = mid;
                    p.vel = vel;
                    p.ang = Mathf.Atan2(diry, dirx);
                    p.spin = Random.Range(-1.2f, 1.2f);
                    p.len = len + thick;               // 两端各延长半个线宽，补上拐角缺口
                    p.wid = Mathf.Max(0.04f, thick * 0.85f);
                    p.col = norm;
                    p.life = Random.Range(1.35f, 1.8f);
                    p.maxLife = p.life;
                    ps[count++] = p;
                }
            }
        }

        void Update()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            int v = 0;
            for (int i = 0; i < count; i++)
            {
                var p = ps[i];
                p.life -= dt;
                if (p.life <= 0f)
                {
                    ps[i] = ps[count - 1];
                    count--;
                    i--;
                    continue;
                }

                p.pos += p.vel * dt;
                p.vel *= 1f - Mathf.Clamp01(1.0f * dt);   // 阻尼 1.0/s → 总共飘出 v/drag ≈ 2~2.8 单位
                p.ang += p.spin * dt;
                ps[i] = p;

                float k = p.life / p.maxLife;

                // 只缩短、不变细：长度线性收到 0，线宽保持不变，看起来像一条线从两端被擦掉。
                // alpha 只在最后 20% 才降，避免缩到亚像素大小时闪烁。
                float shrink = k;
                Color c = p.col;
                c.a *= Mathf.Clamp01(k * 5f);

                float hx = Mathf.Cos(p.ang), hy = Mathf.Sin(p.ang);
                float halfLen = p.len * 0.5f * shrink;
                float halfWid = p.wid * 0.5f;
                Vector3 fwd = new Vector3(hx, hy, 0f) * halfLen;
                Vector3 side = new Vector3(-hy, hx, 0f) * halfWid;

                Vector3 pos = new Vector3(p.pos.x, p.pos.y, 0f);
                verts[v + 0] = pos - fwd - side;
                verts[v + 1] = pos - fwd + side;
                verts[v + 2] = pos + fwd + side;
                verts[v + 3] = pos + fwd - side;
                cols[v + 0] = c; cols[v + 1] = c; cols[v + 2] = c; cols[v + 3] = c;
                v += 4;
            }

            // 把上一帧多画出来的尾巴藏掉
            int tail = lastDrawn * 4;
            for (int i = v; i < tail; i++) verts[i] = Hidden;
            lastDrawn = v / 4;

            if (v > 0 || tail > 0)
            {
                mesh.SetVertices(verts);
                mesh.SetColors(cols);
            }
        }

        public void ClearAll()
        {
            count = 0;
            lastDrawn = 0;
            for (int i = 0; i < verts.Length; i++) verts[i] = Hidden;
            mesh.SetVertices(verts);
        }
    }
}
