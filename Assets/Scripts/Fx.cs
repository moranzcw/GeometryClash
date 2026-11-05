using System.Collections.Generic;
using UnityEngine;

namespace GeoClash
{
    /// <summary>一个会扩张并淡出的光环（爆炸、炸弹冲击波）。</summary>
    public class Ring : MonoBehaviour
    {
        public float T, Dur = 0.4f, MaxR = 5f;
        public Color Col = Color.white;
        Renderer rend;
        bool playing;

        public static Ring Create(Transform parent)
        {
            var go = new GameObject("Ring");
            go.transform.SetParent(parent, false);
            var r = go.AddComponent<Ring>();
            r.rend = MatLib.Attach(go, Meshes.Ring(48, 1f, 0.05f), MatLib.Neon(Color.white));
            return r;
        }

        public void Play(Vector3 pos, Color c, float maxR, float dur)
        {
            transform.position = pos;
            transform.localScale = Vector3.one * 0.2f;
            Col = c;
            MaxR = maxR;
            Dur = Mathf.Max(0.05f, dur);
            T = 0f;
            playing = true;
            rend.sharedMaterial = MatLib.Neon(c);
        }

        void Update()
        {
            if (!playing) return;
            T += Time.deltaTime;
            float k = Mathf.Clamp01(T / Dur);
            float r = Mathf.Lerp(0.3f, MaxR, 1f - (1f - k) * (1f - k));
            // 环的网格建在 XY 平面上，所以要缩放 X/Y，Z 保持 1
            // （之前写成 (r,1,r) 是俯视时代的写法，会导致环只在水平方向拉伸成细长椭圆）
            transform.localScale = new Vector3(r, r, 1f);
            float a = (1f - k) * (1f - k);
            rend.sharedMaterial = MatLib.NeonScaled(Col, a);
            if (k >= 1f)
            {
                playing = false;
                gameObject.SetActive(false);
            }
        }
    }

    /// <summary>
    /// 粒子系统：单网格一次性绘制，容量 4000。
    /// 粒子会与竞技场边界发生弹性碰撞并回弹。
    /// </summary>
    public class Fx : MonoBehaviour
    {
        public static Fx I;

        const int MAX = 6000;
        static readonly Vector3 Hidden = new Vector3(0f, -999f, 0f);

        struct Particle
        {
            public Vector3 pos;
            public Vector3 vel;
            public Color col;
            public float life;
            public float maxLife;
            public float size;
            public float drag;
            public float bounce;    // 0 = 不参与边界碰撞, >0 = 回弹系数
            public float stretch;   // 沿速度方向的拉伸强度
            public bool flash;      // true = 正方形闪斑（爆心、撞击），不随速度拉伸
            public bool soft;       // true = 走柔和贴图那条管线（径向辉光），false = 硬边矩形
        }

        Particle[] ps = new Particle[MAX];
        int count;
        int lastHard = -1, lastSoft = -1;   // 上一帧两条管线各画了几个四边形

        // 粒子分两条渲染管线：
        //   硬边 —— 不带贴图的纯白材质，画出来是边缘锋利的实心矩形（弹片、碎屑）
        //   柔和 —— 带径向辉光贴图，边缘化开（白闪、爆心）
        // 必须拆成两个 Mesh，因为一个 Mesh 只能有一个材质。
        Mesh meshHard, meshSoft;
        Vector3[] vertsHard, vertsSoft;
        Color[] colsHard, colsSoft;
        int[] tris;

        Pool<Ring> ringPool;
        readonly List<Ring> rings = new List<Ring>();

        float shake;
        Camera cam;
        Vector3 camBase;   // 没有 Game 驱动相机时（调试场景）的震动基准位置

        void Awake()
        {
            I = this;

            vertsHard = new Vector3[MAX * 4];
            vertsSoft = new Vector3[MAX * 4];
            colsHard = new Color[MAX * 4];
            colsSoft = new Color[MAX * 4];
            tris = new int[MAX * 6];

            // 每个粒子是一个四边形。UV 照常铺 0..1：
            // 硬边管线的材质不带贴图，采样恒为纯白，所以画出来是实心块；
            // 柔和管线的材质带一张径向辉光贴图，边缘就会自然化开。
            var uvs = new Vector2[MAX * 4];
            for (int i = 0; i < MAX; i++)
            {
                int v = i * 4;
                int t = i * 6;
                tris[t + 0] = v;
                tris[t + 1] = v + 1;
                tris[t + 2] = v + 2;
                tris[t + 3] = v;
                tris[t + 4] = v + 2;
                tris[t + 5] = v + 3;

                uvs[v + 0] = new Vector2(0f, 0f);
                uvs[v + 1] = new Vector2(0f, 1f);
                uvs[v + 2] = new Vector2(1f, 1f);
                uvs[v + 3] = new Vector2(1f, 0f);
            }
            for (int i = 0; i < vertsHard.Length; i++)
            {
                vertsHard[i] = Hidden;
                vertsSoft[i] = Hidden;
            }

            meshHard = BuildMesh("ParticleMesh_Hard", vertsHard, colsHard, uvs, tris, MatLib.Particle);
            meshSoft = BuildMesh("ParticleMesh_Soft", vertsSoft, colsSoft, uvs, tris, MatLib.Glow);

            ringPool = new Pool<Ring>(() => Ring.Create(transform), transform, 20);
        }

        Mesh BuildMesh(string name, Vector3[] v, Color[] c, Vector2[] uv, int[] t, Material mat)
        {
            var m = new Mesh { name = name };
            m.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            m.MarkDynamic();
            m.SetVertices(v);
            m.SetUVs(0, uv);
            m.SetColors(c);
            m.SetTriangles(t, 0);
            m.bounds = new Bounds(Vector3.zero, new Vector3(400f, 400f, 400f));

            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            MatLib.Attach(go, m, mat);
            return m;
        }

        public void SetCamera(Camera c)
        {
            cam = c;
            if (c != null) camBase = c.transform.position;
        }

        // ---------------- 粒子 ----------------

        void Add(Vector3 pos, Vector3 vel, Color c, float life, float size, float drag,
                 float bounce = 0f, float stretch = 0.035f, bool flash = false, bool soft = false)
        {
            if (count >= MAX) return;
            var p = new Particle();
            p.pos = pos;
            p.vel = vel;
            p.col = c;
            p.life = life;
            p.maxLife = life;
            p.size = size;
            p.drag = drag;
            p.bounce = bounce;
            p.stretch = stretch;
            p.flash = flash;
            p.soft = soft;
            ps[count++] = p;
        }

        /// <summary>柔和的圆形白光（爆心、撞击点）：正方形四边形 + 径向辉光贴图。</summary>
        void AddFlash(Vector3 pos, Color c, float life, float size)
        {
            Add(pos, Vector3.zero, c, life, size, 0f, 0f, 0f, true, true);
        }

        /// <summary>通用爆发（拾取、命中火花等）。</summary>
        public void Burst(Vector3 pos, Color c, int n, float speed, float size, float life)
        {
            for (int i = 0; i < n; i++)
            {
                float a = Random.value * Mathf.PI * 2f;
                float s = speed * Random.Range(0.35f, 1f);
                Vector3 dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);
                Vector3 vel = dir * s + new Vector3(0f, Random.Range(0f, 1.2f), 0f);
                Add(pos, vel, c, life * Random.Range(0.6f, 1.15f), size * Random.Range(0.6f, 1.2f), 3.2f);
            }
        }

        /// <summary>
        /// 敌人爆炸（游戏内默认参数）：柔和白闪 + 主弹片 + 震屏/网格。
        /// 角度/速度/尺寸/寿命全部随机，避免出现"均匀一圈"的机械感。
        /// </summary>
        public void Explosion(Vector3 pos, Color c, float scale)
        {
            Explosion(pos, c, scale, ExplosionProfile.Default);
        }

        /// <summary>敌人爆炸，参数由外部传入（调试场景用）。</summary>
        public void Explosion(Vector3 pos, Color c, float scale, ExplosionProfile p)
        {
            if (p == null) p = ExplosionProfile.Default;
            scale = Mathf.Clamp(scale, 0.5f, 3.5f);

            // 白光闪烁：内层纯白 + 外层淡光晕，两层都是径向柔和辉光，没有硬边方框
            if (p.flashOn)
            {
                AddFlash(pos, Palette.White, p.flashLife, p.flashSize * scale);

                var outer = Palette.White;
                outer.a = p.flashOuterStrength;
                AddFlash(pos, outer, p.flashLife * p.flashOuterTime,
                    p.flashSize * p.flashOuterScale * scale);
            }

            // 主弹片：飞得远，必定撞墙回弹
            if (p.shardsOn) Shards(pos, c, scale, p);

            if (p.shakeOn)
                Shake(Mathf.Lerp(p.shakeMin, p.shakeMax, Mathf.Clamp01(scale / 3f)));

            if (p.gridOn)
                SpringGrid.Push(new Vector2(pos.x, pos.y), p.gridPushRadius * scale, p.gridPushStrength * scale);
        }

        /// <summary>
        /// 主弹片。敌人爆炸、主角死亡、主角诞生都走这一套，保证弹片尺寸/速度/寿命完全一致。
        /// </summary>
        void Shards(Vector3 pos, Color c, float scale, ExplosionProfile p)
        {
            int shards = Mathf.RoundToInt(p.shardCount * Mathf.Clamp(scale, 0.7f, 2f));
            for (int i = 0; i < shards; i++)
            {
                float a = Random.value * Mathf.PI * 2f;
                Vector3 dir = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f);

                // 弹片保留敌人自己的颜色，只掺一点点白。
                // 原来掺 30%~70% 白，叠加混合下几十个弹片一叠就全糊成白色了。
                Color sc = Color.Lerp(c, Palette.White, Random.Range(0f, 0.18f));
                sc.r *= 0.78f;
                sc.g *= 0.78f;
                sc.b *= 0.78f;

                Add(pos + dir * Random.Range(0f, 0.3f), dir * ExplosionProfile.Rand(p.shardSpeed),
                    sc,
                    ExplosionProfile.Rand(p.shardLife),
                    ExplosionProfile.Rand(p.shardSize) * scale,
                    ExplosionProfile.Rand(p.shardDrag), 0.92f,
                    ExplosionProfile.Rand(p.shardStretch));
            }
        }

        /// <summary>主角诞生：和爆炸同款白闪 + 弹片，但不震屏、不推网格（推力由复活黑洞负责）。</summary>
        public void PlayerSpawn(Vector3 pos, Color c)
        {
            var p = ExplosionProfile.Default;
            AddFlash(pos, Palette.White, p.flashLife, p.flashSize * 1.1f);
            Shards(pos, c, 1.1f, p);
        }

        /// <summary>
        /// 撞击点爆开：子弹打到竞技场边界时调用。
        /// normal 指向场地内部，碎屑朝内飞散并继续与边界回弹。
        /// </summary>
        public void Impact(Vector3 pos, Vector2 normal, Color c, float scale)
        {
            scale = Mathf.Clamp(scale, 0.4f, 2f);

            AddFlash(pos + Vector3.up * 0.06f, Palette.White, 0.07f, 1.0f * scale);

            int n = Mathf.RoundToInt(10f * scale) + 5;
            for (int i = 0; i < n; i++)
            {
                Vector2 rnd = Random.insideUnitCircle;
                Vector2 dir = normal + rnd * 0.95f;
                if (dir.sqrMagnitude < 0.0001f) dir = normal;
                dir.Normalize();

                Vector3 vel = new Vector3(dir.x, dir.y, 0f) * Random.Range(4f, 22f);

                Add(pos + new Vector3(normal.x, normal.y, 0f) * 0.10f, vel,
                    Random.value < 0.4f ? Palette.White : c,
                    Random.Range(0.2f, 0.65f),
                    Random.Range(0.08f, 0.26f) * scale,
                    Random.Range(2f, 4.5f), 0.88f, 0.05f);
            }

            // 不再画冲击波圆环：子弹撞边太频繁，一圈圈光环会把画面刷得很花
            SpringGrid.Push(new Vector2(pos.x, pos.y), 2.4f, 30f * scale);
        }

        /// <summary>
        /// 玩家拖尾：从机身背面边缘持续喷出的粒子流。
        /// tailPos 已经是"边缘上的点"（由 Player.EdgePoint 算好），这里只负责在边缘切向上铺开一点宽度。
        /// </summary>
        public void PlayerTrail(Vector3 tailPos, Vector2 dir, float speed01)
        {
            // 蛇形摆动：整束尾焰绕着喷射轴左右来回摆。
            // 因为粒子离开后不再转向，摆动会在身后留下一条波浪形轨迹。
            const float swayAmp = 3f * Mathf.Deg2Rad;
            const float swayFreq = 22f;                  // 约 0.29 秒一个来回
            float sway = Mathf.Sin(Time.time * swayFreq) * swayAmp;

            // 喷射方向 = 移动反方向；perp 是沿机身边缘的切向，用来把粒子铺成一条有宽度的口子
            Vector2 back2 = new Vector2(-dir.x, -dir.y);
            if (back2.sqrMagnitude < 1e-6f) back2 = Vector2.down;
            back2.Normalize();
            Vector2 perp = new Vector2(-back2.y, back2.x);

            int n = 2 + Mathf.RoundToInt(speed01 * 1f);
            for (int i = 0; i < n; i++)
            {
                // 起点已经在边缘上，所以偏移只沿边缘切向铺开（±0.26），
                // 再顺着喷射方向轻推 0~0.1，避免粒子贴着机身闪。
                float along = Random.Range(-0.26f, 0.26f);
                float outw = Random.Range(0f, 0.10f);
                Vector3 off = new Vector3(
                    perp.x * along + back2.x * outw,
                    perp.y * along + back2.y * outw,
                    0f);

                // 喷射初速度：沿移动反方向抛出。锥角收窄到 ±0.75°（总宽 1.5°），
                // 所以喷出来是一束很细的尾焰，而不是扇形散开。
                float spd = Mathf.Lerp(8f, 24f, speed01) * Random.Range(0.88f, 1.12f);
                const float halfCone = 0.0131f;              // 0.75°
                float ang = sway + Random.Range(-halfCone, halfCone);
                float cs = Mathf.Cos(ang), sn = Mathf.Sin(ang);
                Vector3 v = new Vector3(back2.x * cs - back2.y * sn, back2.x * sn + back2.y * cs, 0f) * spd;

                // 暖黄火星：在深橙到亮黄之间随机，少量白热
                Color c = Random.value < 0.12f
                    ? Palette.White
                    : Color.Lerp(Palette.Trail, Palette.TrailHot, Random.value);

                // 只压 RGB，alpha 保持 1，亮度衰减更可预测（叠加混合的最终亮度 = rgb × alpha）
                float bright = Random.Range(0.18f, 0.28f);
                c = new Color(c.r * bright, c.g * bright, c.b * bright, 1f);

                Add(tailPos + off, v, c,
                    Random.Range(0.28f, 0.52f),
                    Random.Range(0.20f, 0.44f),
                    Random.Range(1.4f, 1.8f), 0f, 0.03f);
            }
        }

        public void Spark(Vector3 pos, Vector2 dir, Color c)
        {
            Vector3 d = new Vector3(dir.x, dir.y, 0f);
            if (d.sqrMagnitude < 0.001f) d = Vector3.up;
            d.Normalize();
            for (int i = 0; i < 4; i++)
            {
                Vector3 v = d * Random.Range(6f, 14f) + new Vector3(Random.Range(-3f, 3f), Random.Range(0f, 2f), Random.Range(-3f, 3f));
                Add(pos, v, c, Random.Range(0.12f, 0.26f), 0.26f, 5f, 0.5f, 0.05f);
            }
        }

        /// <summary>标题界面的环境浮尘。</summary>
        public void AddAmbient(Vector3 pos, Color c)
        {
            Vector3 v = new Vector3(Random.Range(-0.6f, 0.6f), Random.Range(0.3f, 0.9f), Random.Range(-0.6f, 0.6f));
            Add(pos, v, c, Random.Range(1.1f, 2.4f), Random.Range(0.18f, 0.42f), 0.5f);
        }

        public void Shockwave(Vector3 pos, Color c, float maxR, float dur)
        {
            Ring r = null;
            for (int i = 0; i < rings.Count; i++)
            {
                if (rings[i] != null && !rings[i].gameObject.activeSelf) { r = rings[i]; break; }
            }
            if (r == null)
            {
                r = ringPool.Get();
                rings.Add(r);
            }
            else
            {
                r.gameObject.SetActive(true);
            }
            r.Play(pos, c, maxR, dur);
        }

        public void Shake(float amount)
        {
            shake = Mathf.Max(shake, amount);
        }

        // ---------------- 每帧 ----------------

        void Update()
        {
            float dt = Time.deltaTime;

            int vh = 0;   // 硬边管线的顶点写指针
            int vs = 0;   // 柔和管线的顶点写指针

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
                p.vel *= 1f - Mathf.Clamp01(p.drag * dt);

                // 与竞技场边界弹性碰撞
                if (p.bounce > 0f)
                {
                    if (p.pos.x > Game.AX) { p.pos.x = Game.AX; p.vel.x = -Mathf.Abs(p.vel.x) * p.bounce; }
                    else if (p.pos.x < -Game.AX) { p.pos.x = -Game.AX; p.vel.x = Mathf.Abs(p.vel.x) * p.bounce; }

                    if (p.pos.y > Game.AY) { p.pos.y = Game.AY; p.vel.y = -Mathf.Abs(p.vel.y) * p.bounce; }
                    else if (p.pos.y < -Game.AY) { p.pos.y = -Game.AY; p.vel.y = Mathf.Abs(p.vel.y) * p.bounce; }
                }

                ps[i] = p;

                float k = p.life / p.maxLife;
                // 用 sqrt 而不是平方，让粒子在大部分寿命里保持较实的亮度，只在最后才淡出
                float a = Mathf.Sqrt(k);
                Color c = p.col;
                c.a *= a;

                float sz = p.size * Mathf.Lerp(0.28f, 1f, k);
                float sp = p.vel.magnitude;

                float halfLen, halfWid;
                if (p.flash)
                {
                    // 白闪：正方形，不随速度变形（贴图是径向辉光，所以看起来是柔和的圆团）
                    halfLen = sz;
                    halfWid = sz;
                }
                else
                {
                    // 矩形碎片：沿运动方向拉长、横向压窄（边缘是硬的，不做渐变收边）
                    halfLen = sz * (1.14f + sp * p.stretch * 0.6f);
                    halfWid = sz * 0.30f;
                }

                Vector3 dir = sp > 0.001f ? p.vel / sp : Vector3.up;
                Vector3 side = new Vector3(-dir.y, dir.x, 0f) * halfWid;
                Vector3 fwd = new Vector3(dir.x, dir.y, 0f) * halfLen;

                // 按粒子类型写进对应的那条管线
                if (p.soft)
                {
                    vertsSoft[vs + 0] = p.pos - fwd - side;
                    vertsSoft[vs + 1] = p.pos - fwd + side;
                    vertsSoft[vs + 2] = p.pos + fwd + side;
                    vertsSoft[vs + 3] = p.pos + fwd - side;
                    colsSoft[vs + 0] = c; colsSoft[vs + 1] = c;
                    colsSoft[vs + 2] = c; colsSoft[vs + 3] = c;
                    vs += 4;
                }
                else
                {
                    vertsHard[vh + 0] = p.pos - fwd - side;
                    vertsHard[vh + 1] = p.pos - fwd + side;
                    vertsHard[vh + 2] = p.pos + fwd + side;
                    vertsHard[vh + 3] = p.pos + fwd - side;
                    colsHard[vh + 0] = c; colsHard[vh + 1] = c;
                    colsHard[vh + 2] = c; colsHard[vh + 3] = c;
                    vh += 4;
                }
            }

            UploadMesh(meshHard, vertsHard, colsHard, vh, ref lastHard);
            UploadMesh(meshSoft, vertsSoft, colsSoft, vs, ref lastSoft);

            if (cam != null)
            {
                shake = Mathf.Max(0f, shake - dt * 2.2f);
                float s = shake * shake;
                Vector3 off = new Vector3(Random.Range(-1f, 1f), Random.Range(-1f, 1f), Random.Range(-1f, 1f)) * s * 1.6f;
                // 正式游戏里相机每帧被 Game 驱动，用它的基准位置；调试场景没有 Game，用记下来的位置
                Vector3 b = Game.I != null ? Game.I.CamBase : camBase;
                cam.transform.position = b + off;
            }
        }

        /// <summary>
        /// 把这一帧写好的顶点上传到网格，并把"上一帧用过、这一帧不用"的尾巴藏起来。
        /// 整块上传（数组多大就传多大）：MAX 个四边形 24000 顶点，实测开销可以忽略，
        /// 用 SetVertices(array, start, length) 分段上传并不会更快，反而容易出坑。
        /// </summary>
        void UploadMesh(Mesh m, Vector3[] v, Color[] c, int used, ref int last)
        {
            int tail = last * 4;
            for (int i = used; i < tail; i++) v[i] = Hidden;
            last = used / 4;

            if (used > 0 || tail > 0)
            {
                m.SetVertices(v);
                m.SetColors(c);
            }
        }

        public void ClearAll()
        {
            count = 0;
            lastHard = 0;
            lastSoft = 0;
            for (int i = 0; i < vertsHard.Length; i++)
            {
                vertsHard[i] = Hidden;
                vertsSoft[i] = Hidden;
            }
            meshHard.SetVertices(vertsHard);
            meshSoft.SetVertices(vertsSoft);
            for (int i = 0; i < rings.Count; i++)
            {
                if (rings[i] != null) rings[i].gameObject.SetActive(false);
            }
            shake = 0f;
        }
    }
}
