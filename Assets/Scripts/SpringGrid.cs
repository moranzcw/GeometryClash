using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

namespace GeoClash
{
    /// <summary>
    /// 弹簧质点网格（mass-spring），Burst + Jobs 实现。
    ///
    /// 模拟格边长 = 视觉上的"小格"（0.35），所以每一个小格都真正参与物理运算：
    /// 每个质点与四邻用弹簧相连、并锚定在静止位置，场地边界上的质点被钉死（鼓面模型）。
    ///
    /// 渲染用共享顶点：网格顶点就是质点本身（一个质点一个顶点），
    /// 三角形按格索引进，UV 直接写"格坐标"(c, r)，
    /// 大格线 / 小格线 / 顶点圆点全部由 GridCell 着色器按这个坐标在片元里算出来。
    /// 因为 UV 是全局格坐标，图案天然跨格连续，不需要按分辨率对齐。
    /// </summary>
    public class SpringGrid : MonoBehaviour
    {
        public static SpringGrid I;

        /// <summary>模拟格边长（= 视觉小格）。0.28 → 0.35，质点总数少了约 36%。</summary>
        public const float Cell = 0.35f;

        /// <summary>
        /// 每多少个模拟格画一条大格线。
        /// 必须能整除横向/纵向的模拟格数，4 正好：236/4 = 59、140/4 = 35。
        /// 大格边长 = 4 × 0.35 = 1.4，和改之前完全一致（场地还是 59 × 35 个大格）。
        /// </summary>
        public const int MajorEvery = 4;

        /// <summary>
        /// 每个大格里画多少条小格线（纯视觉参数，不影响模拟）。
        /// 小格周期 = MajorEvery / SubCells 个模拟格；这里取 4，周期正好 1 个模拟格，
        /// 也就是视觉小格和模拟格完全重合（0.35 世界单位）。
        /// 以后想在不改模拟分辨率的前提下让线更稀，把这个值调小即可（比如 2 → 每 2 个模拟格一条）。
        /// </summary>
        public const int SubCells = 4;

        // 物理参数。
        // 波速 c = Cell·√SpringK，所以改 Cell 必须按 1/Cell² 反比调 SpringK 才能保持同样的波速。
        // 格距 0.28 → 0.35（×1.25），K 就要 ÷1.5625：2375 → 1520。
        // 两边波速都是 0.35×√1520 = 0.28×√2375 = 13.65 世界单位/秒。
        // AnchorK 不用动（单位是 1/s²，和格距无关），形变衰减长度 λ = √(K/Anchor) 换算成
        // 世界单位仍然是 √(1520/9)×0.35 = √(2375/9)×0.28 = 4.55，和原来一致。
        const float SpringK = 1520f;
        const float AnchorK = 9f;
        const float Damping = 3.2f;

        // 粘性阻尼（速度的拉普拉斯）：只衰减"相对邻居的速度差"。
        // 这是抑制高频抖动的关键 —— 均匀阻尼 Damping 对高频和低频的衰减速率一样，
        // 但格距缩小 5 倍后高频模态的振荡频率也高了 5 倍，看上去就是一层持续的"锯齿嗡鸣"。
        // 粘性项对波长 2~3 格的抖动衰减率约 12~30/s（0.1 秒内消失），
        // 对波长 20 格以上的大波纹只有 0.4/s（几乎无影响）。
        const float Viscosity = 4f;

        const float MaxOffset = 7f;

        // 单个子步上限：显式积分的稳定条件是 h < 2/√(8K)。
        // K = 1520 时上限约 0.0181，这里取 0.006 留了 3 倍余量。
        const float MaxStep = 0.006f;

        int cols, rows, n;
        NativeArray<float2> rest;
        NativeArray<float2> velA;
        NativeArray<float2> velB;
        NativeArray<float2> posA;
        NativeArray<float2> posB;
        NativeArray<byte> pinned;
        NativeArray<Vector3> verts;

        Mesh mesh;
        bool aIsCurrent = true;
        bool ready;

        // 黑洞吸附的持续状态（复活时由 Player 触发）
        Vector2 suckPos;
        SuctionProfile suckProfile;
        float suckDur, suckTime;

        // 位移限幅。平时 7，防止爆炸把线条折成一团；
        // 黑洞吸附期间要放开到作用半径，否则 0.5 秒就顶住上限、网格又"塌到位就静止"。
        // 吸附结束后按 40/秒 收回到 MaxOffset，避免残留的大位移被瞬间夹平。
        float maxOffsetCur = MaxOffset;

        // ------------------------------------------------------------ 生命周期

        void Awake()
        {
            I = this;
            Build();
        }

        void OnDestroy()
        {
            if (I == this) I = null;
            Release();
        }

        void Release()
        {
            ready = false;
            if (rest.IsCreated) rest.Dispose();
            if (velA.IsCreated) velA.Dispose();
            if (velB.IsCreated) velB.Dispose();
            if (posA.IsCreated) posA.Dispose();
            if (posB.IsCreated) posB.Dispose();
            if (pinned.IsCreated) pinned.Dispose();
            if (verts.IsCreated) verts.Dispose();
        }

        // ------------------------------------------------------------ 构建

        void Build()
        {
            cols = Mathf.RoundToInt(2f * Game.AX / Cell) + 1;
            rows = Mathf.RoundToInt(2f * Game.AY / Cell) + 1;
            n = cols * rows;

            rest = new NativeArray<float2>(n, Allocator.Persistent);
            velA = new NativeArray<float2>(n, Allocator.Persistent);
            velB = new NativeArray<float2>(n, Allocator.Persistent);
            posA = new NativeArray<float2>(n, Allocator.Persistent);
            posB = new NativeArray<float2>(n, Allocator.Persistent);
            pinned = new NativeArray<byte>(n, Allocator.Persistent);
            verts = new NativeArray<Vector3>(n, Allocator.Persistent);

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int i = r * cols + c;
                    float2 p = new float2(-Game.AX + c * Cell, -Game.AY + r * Cell);
                    rest[i] = p;
                    posA[i] = p;
                    posB[i] = p;
                    // 边界一圈钉死
                    pinned[i] = (byte)((c == 0 || c == cols - 1 || r == 0 || r == rows - 1) ? 1 : 0);
                }
            }

            // 共享顶点：一个质点一个顶点，UV 直接是格坐标 (c, r)
            int cellCount = (cols - 1) * (rows - 1);
            var tris = new int[cellCount * 6];
            var uvs = new Vector2[n];

            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    uvs[r * cols + c] = new Vector2(c, r);

            int ti = 0;
            for (int r = 0; r < rows - 1; r++)
            {
                for (int c = 0; c < cols - 1; c++)
                {
                    int i0 = r * cols + c;
                    int i1 = i0 + 1;
                    int i2 = i0 + cols + 1;
                    int i3 = i0 + cols;

                    tris[ti++] = i0; tris[ti++] = i1; tris[ti++] = i2;
                    tris[ti++] = i0; tris[ti++] = i2; tris[ti++] = i3;
                }
            }

            mesh = new Mesh { name = "SpringGrid" };
            mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.MarkDynamic();
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.bounds = new Bounds(Vector3.zero, new Vector3(400f, 400f, 400f));

            MatLib.Attach(gameObject, mesh, MatLib.GridCell);

            ready = true;
            UploadMesh();
        }

        // ------------------------------------------------------------ 扰动

        /// <summary>在世界坐标处给网格一个向外的冲量（strength 越大形变越猛）。</summary>
        public static void Push(Vector2 p, float radius, float strength)
        {
            if (I == null) return;
            I.ApplyPush(p, radius, strength);
        }

        /// <summary>
        /// 定向推力：把半径内的质点沿 dir 方向顶出去，形成一道跟着物体前进的弓形波。
        /// 爆炸/炸弹仍然用径向的 Push，只有子弹这类"有明确前进方向"的东西才用它。
        /// </summary>
        public static void PushDirected(Vector2 p, Vector2 dir, float radius, float strength)
        {
            if (I == null) return;
            I.ApplyPushDirected(p, dir, radius, strength);
        }

        /// <summary>
        /// 黑洞：在 duration 秒内对半径内的质点持续施加指向中心的引力。
        /// 引力 = strength / (dist^power + soften)，**越近越强**，所以质点会一路加速往中心掉，
        /// 整个网格持续坍缩，而不是"塌到某个位置就停住"。
        /// </summary>
        public static void Suction(Vector2 p, SuctionProfile profile)
        {
            if (I == null) return;
            var sp = profile != null ? profile : SuctionProfile.Default;
            I.suckPos = p;
            I.suckProfile = sp;
            I.suckDur = Mathf.Max(0.05f, sp.duration);
            I.suckTime = I.suckDur;
            // 放开位移限幅，让质点能一路掉到中心
            I.maxOffsetCur = Mathf.Max(MaxOffset, sp.radius);
        }

        void ApplyPush(Vector2 p, float radius, float strength)
        {
            if (!ready || radius <= 0.001f || strength == 0f) return;

            float2 pf = new float2(p.x, p.y);

            int c0 = Mathf.Max(0, Mathf.FloorToInt((p.x - radius + Game.AX) / Cell));
            int c1 = Mathf.Min(cols - 1, Mathf.CeilToInt((p.x + radius + Game.AX) / Cell));
            int r0 = Mathf.Max(0, Mathf.FloorToInt((p.y - radius + Game.AY) / Cell));
            int r1 = Mathf.Min(rows - 1, Mathf.CeilToInt((p.y + radius + Game.AY) / Cell));

            float r2 = radius * radius;
            var vel = aIsCurrent ? velA : velB;   // 写到最新的那一份，下一子步会读它

            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    int i = r * cols + c;
                    if (pinned[i] != 0) continue;

                    float2 d = rest[i] - pf;
                    float d2 = math.dot(d, d);
                    if (d2 > r2 || d2 < 1e-6f) continue;

                    float dist = math.sqrt(d2);
                    float k = 1f - dist / radius;
                    vel[i] += (d / dist) * (strength * k * k);
                }
            }
        }

        void ApplyPushDirected(Vector2 p, Vector2 dir, float radius, float strength)
        {
            if (!ready || radius <= 0.001f || strength == 0f) return;

            float2 pf = new float2(p.x, p.y);
            float2 df = new float2(dir.x, dir.y);
            float dl = math.length(df);
            if (dl < 1e-5f) return;
            df /= dl;

            // 采样中心前移半个半径：波峰落在子弹前方，看起来像被"顶"出来的水波
            float2 center = pf + df * (radius * 0.5f);

            int c0 = Mathf.Max(0, Mathf.FloorToInt((center.x - radius + Game.AX) / Cell));
            int c1 = Mathf.Min(cols - 1, Mathf.CeilToInt((center.x + radius + Game.AX) / Cell));
            int r0 = Mathf.Max(0, Mathf.FloorToInt((center.y - radius + Game.AY) / Cell));
            int r1 = Mathf.Min(rows - 1, Mathf.CeilToInt((center.y + radius + Game.AY) / Cell));

            float r2 = radius * radius;
            var vel = aIsCurrent ? velA : velB;

            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    int i = r * cols + c;
                    if (pinned[i] != 0) continue;

                    float2 d = rest[i] - center;
                    float d2 = math.dot(d, d);
                    if (d2 > r2 || d2 < 1e-6f) continue;

                    float dist = math.sqrt(d2);
                    float k = 1f - dist / radius;
                    k *= k;

                    // 主体沿飞行方向推，另加一点由子弹位置向外的分量，
                    // 让质点不是整体平移，而是被挤开一条"沟"
                    float2 radial = rest[i] - pf;
                    float rl = math.length(radial);
                    float2 side = rl > 1e-5f ? radial / rl : float2.zero;

                    vel[i] += (df * 0.85f + side * 0.35f) * (strength * k);
                }
            }
        }

        /// <summary>
        /// 黑洞引力场。每帧对半径内的质点施加指向中心的加速度：
        ///     a = strength * edge / (dist^power + soften)
        /// 距离越近 a 越大 → 质点一路加速下坠，网格持续坍缩（不会塌到某处就静止）。
        /// edge 只负责在作用半径边缘平滑收尾，避免边界上一圈突然断掉。
        /// </summary>
        void ApplySuction(float dt)
        {
            if (suckTime <= 0f) return;
            suckTime -= dt;

            var sp = suckProfile != null ? suckProfile : SuctionProfile.Default;
            float radius = Mathf.Max(0.5f, sp.radius);

            float elapsed = suckDur - suckTime;
            float fadeIn = Mathf.Max(0.01f, sp.fadeIn);
            float fadeOut = Mathf.Max(0.01f, sp.fadeOut);
            float ramp = Mathf.Clamp01(elapsed / fadeIn);
            float fade = Mathf.Clamp01(suckTime / fadeOut);
            float amp = ramp * fade;

            float2 pf = new float2(suckPos.x, suckPos.y);

            int c0 = Mathf.Max(0, Mathf.FloorToInt((suckPos.x - radius + Game.AX) / Cell));
            int c1 = Mathf.Min(cols - 1, Mathf.CeilToInt((suckPos.x + radius + Game.AX) / Cell));
            int r0 = Mathf.Max(0, Mathf.FloorToInt((suckPos.y - radius + Game.AY) / Cell));
            int r1 = Mathf.Min(rows - 1, Mathf.CeilToInt((suckPos.y + radius + Game.AY) / Cell));

            var pos = aIsCurrent ? posA : posB;
            var vel = aIsCurrent ? velA : velB;

            float power = Mathf.Max(0.1f, sp.power);
            float capture = Mathf.Max(0.05f, sp.capture);

            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    int i = r * cols + c;
                    if (pinned[i] != 0) continue;

                    // 用**当前**位置算方向，质点才会一路被拽着往中心掉
                    float2 toC = pf - pos[i];
                    float dist = math.length(toC);
                    if (dist > radius) continue;

                    // 已经掉进中心：钉住，不再继续加速（否则会穿过去翻面）
                    if (dist < capture)
                    {
                        vel[i] = float2.zero;
                        continue;
                    }

                    float2 dir = toC / dist;

                    // 外圈平滑收尾
                    float edge = 1f - dist / radius;
                    edge = edge * edge * (3f - 2f * edge);

                    float a = sp.strength * amp * edge / (Mathf.Pow(dist, power) + sp.soften);
                    vel[i] += dir * (a * dt);

                    // 防穿越：一帧内朝中心的位移不超过当前距离的 40%，
                    // 否则高加速度下质点会一步跨过圆心，跑到对面去
                    float vIn = math.dot(vel[i], dir);
                    float vCap = dist / (dt * 2.5f);
                    if (vIn > vCap) vel[i] -= dir * (vIn - vCap);
                }
            }
        }

        /// <summary>
        /// 敌人力场对网格的持续作用。引力井把质点往中心拽（塌出一个坑），
        /// 黑洞把质点往外推（鼓出一个包）。每帧施加一次加速度，和弹簧回弹平衡后
        /// 形成一块**静态形变**，跟着敌人一起移动。
        ///
        /// 方向和衰减都按**静止位置**算（rest），不按当前位置 ——
        /// 否则质点被拉近后力会继续变大，会一路塌进中心收不回来。
        /// </summary>
        void ApplyRadialField(Vector2 center, float radius, float strength, float dt)
        {
            if (!ready || radius <= 0.001f || strength == 0f) return;

            float2 pf = new float2(center.x, center.y);

            int c0 = Mathf.Max(0, Mathf.FloorToInt((center.x - radius + Game.AX) / Cell));
            int c1 = Mathf.Min(cols - 1, Mathf.CeilToInt((center.x + radius + Game.AX) / Cell));
            int r0 = Mathf.Max(0, Mathf.FloorToInt((center.y - radius + Game.AY) / Cell));
            int r1 = Mathf.Min(rows - 1, Mathf.CeilToInt((center.y + radius + Game.AY) / Cell));

            float r2 = radius * radius;
            var vel = aIsCurrent ? velA : velB;

            for (int r = r0; r <= r1; r++)
            {
                for (int c = c0; c <= c1; c++)
                {
                    int i = r * cols + c;
                    if (pinned[i] != 0) continue;

                    float2 d = rest[i] - pf;
                    float d2 = math.dot(d, d);
                    if (d2 > r2 || d2 < 1e-4f) continue;

                    float dist = math.sqrt(d2);
                    float edge = 1f - dist / radius;
                    edge = edge * edge * (3f - 2f * edge);   // smoothstep：边界不留硬环

                    vel[i] += (d / dist) * (strength * edge * dt);
                }
            }
        }

        /// <summary>遍历场上存活的引力井 / 黑洞，把各自的力场加到网格上。</summary>
        void ApplyEnemyFields(float dt)
        {
            Enemy.PruneFields();          // 每帧兜底清理一次，列表只有 2~5 个，开销可忽略

            var list = Enemy.Fields;
            if (list == null || list.Count == 0) return;

            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null || e.Dead) continue;
                Vector2 p = new Vector2(e.Pos.x, e.Pos.y);

                if (e.Kind == EnemyKind.Gravity)
                    ApplyRadialField(p, Enemy.GravityGridRadius, Enemy.GravityGridAccel, dt);
                else if (e.Kind == EnemyKind.Hole)
                    ApplyRadialField(p, Enemy.HoleGridRadius, Enemy.HoleGridAccel, dt);
            }
        }

        // ------------------------------------------------------------ 模拟

        void LateUpdate()
        {
            if (!ready) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            dt = Mathf.Min(dt, 0.05f);

            // 敌人力场（引力井/黑洞）和黑洞吸附都是"持续施力"，每帧施加一次，
            // 必须在子步积分之前
            ApplyEnemyFields(dt);
            ApplySuction(dt);

            // 吸附结束后把限幅慢慢收回来
            if (suckTime <= 0f && maxOffsetCur > MaxOffset)
                maxOffsetCur = Mathf.MoveTowards(maxOffsetCur, MaxOffset, 40f * dt);

            int steps = Mathf.Clamp(Mathf.CeilToInt(dt / MaxStep), 2, 8);
            float h = dt / steps;
            for (int s = 0; s < steps; s++) Step(h);

            UploadMesh();
        }

        void Step(float h)
        {
            var src = aIsCurrent ? posA : posB;
            var dst = aIsCurrent ? posB : posA;
            var vsrc = aIsCurrent ? velA : velB;
            var vdst = aIsCurrent ? velB : velA;

            new SimJob
            {
                rest = rest,
                src = src,
                dst = dst,
                velSrc = vsrc,
                velDst = vdst,
                pinned = pinned,
                cols = cols,
                rows = rows,
                h = h,
                cell = Cell,
                springK = SpringK,
                anchorK = AnchorK,
                damping = Damping,
                viscosity = Viscosity,
                maxOff = maxOffsetCur
            }.Schedule(n, 64).Complete();

            aIsCurrent = !aIsCurrent;
        }

        [BurstCompile]
        struct SimJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> rest;
            [ReadOnly] public NativeArray<float2> src;
            [ReadOnly] public NativeArray<float2> velSrc;
            [ReadOnly] public NativeArray<byte> pinned;
            public NativeArray<float2> dst;
            public NativeArray<float2> velDst;
            public int cols, rows;
            public float h, cell, springK, anchorK, damping, viscosity, maxOff;

            public void Execute(int i)
            {
                if (pinned[i] != 0)
                {
                    dst[i] = src[i];
                    velDst[i] = float2.zero;
                    return;
                }

                int r = i / cols;
                int c = i - r * cols;
                float2 p = src[i];
                float2 f = float2.zero;

                if (c > 0) f += Spring(p, src[i - 1]);
                if (c < cols - 1) f += Spring(p, src[i + 1]);
                if (r > 0) f += Spring(p, src[i - cols]);
                if (r < rows - 1) f += Spring(p, src[i + cols]);

                f += (rest[i] - p) * anchorK;

                float2 v0 = velSrc[i];
                f -= v0 * damping;

                // 粘性阻尼：力的拉普拉斯 = 4·(邻居速度均值 − 自身速度)。
                // 只惩罚"和邻居不一致"的速度分量 —— 波长 2~3 格的抖动衰减率约 12~30/s，
                // 波长 20 格以上的大波纹只有 0.4/s，所以能吃掉锯齿感又不影响大浪。
                float2 vAvg = (velSrc[i - 1] + velSrc[i + 1] + velSrc[i - cols] + velSrc[i + cols]) * 0.25f;
                f += (vAvg - v0) * (viscosity * 4f);

                float2 v = v0 + f * h;
                float2 np = p + v * h;

                float2 off = np - rest[i];
                float m2 = math.dot(off, off);
                float lim = maxOff * maxOff;
                if (m2 > lim)
                {
                    np = rest[i] + off * (maxOff / math.sqrt(m2));
                    v *= 0.4f;
                }

                velDst[i] = v;
                dst[i] = np;
            }

            float2 Spring(float2 p, float2 q)
            {
                float2 d = q - p;
                float l2 = math.dot(d, d);
                if (l2 < 1e-10f) return float2.zero;
                float l = math.sqrt(l2);
                return d * ((l - cell) * springK / l);
            }
        }

        // ------------------------------------------------------------ 出网格

        void UploadMesh()
        {
            new VertJob
            {
                pos = aIsCurrent ? posA : posB,
                verts = verts
            }.Schedule(n, 256).Complete();

            mesh.SetVertices(verts);
        }

        [BurstCompile]
        struct VertJob : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float2> pos;
            public NativeArray<Vector3> verts;

            public void Execute(int i)
            {
                float2 p = pos[i];
                verts[i] = new Vector3(p.x, p.y, 0f);
            }
        }

        public void ResetGrid()
        {
            if (!ready) return;
            suckTime = 0f;      // 重开一局时别把上一局的黑洞带过来
            maxOffsetCur = MaxOffset;
            for (int i = 0; i < n; i++)
            {
                posA[i] = rest[i];
                posB[i] = rest[i];
                velA[i] = float2.zero;
                velB[i] = float2.zero;
            }
            UploadMesh();
        }
    }
}
