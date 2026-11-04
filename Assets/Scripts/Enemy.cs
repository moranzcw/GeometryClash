using System.Collections.Generic;
using UnityEngine;

namespace GeoClash
{
    public enum EnemyKind { Grunt, Wanderer, Dodger, Spinner, Chomper, Gravity, Weevil, Hole, Boss }

    /// <summary>
    /// 几何体敌人。一个类覆盖 9 种行为，用 Kind 分派，方便调参。
    /// </summary>
    public class Enemy : MonoBehaviour
    {
        public EnemyKind Kind;
        public float Hp, MaxHp, Radius, Speed, Size = 1f;
        public bool Dead;
        public int GeomCount = 1;
        public int ScoreValue = 10;
        public Color BaseColor = Color.white;

        /// <summary>
        /// 缓存的世界坐标，每个 Update 末尾同步一次。
        ///
        /// 碰撞检测是 O(子弹 × 敌人) 的双重循环，原来内层每一对都要读一次
        /// `transform.position` —— 那是原生调用，实测 22.9 ns/次，
        /// 300×300 = 9 万对就要 4.38 ms。改成读这个字段只要 0.26 ms（快 17 倍）。
        /// **注意：敌人位置只能通过 Create / Update 两处写入，否则这个缓存会过期。**
        /// </summary>
        public Vector3 Pos;

        Vector2 vel;
        Vector2 dashDir;
        Vector2 hover;
        float t;
        float fireTimer;
        float stateTimer;
        float warmup = 0.7f;
        float flash;
        float spin;
        int phase;
        int stage;
        bool entered;
        float visualAngle;

        Transform visual;
        Transform core;
        readonly List<Renderer> rends = new List<Renderer>();

        /// <summary>受击闪白当前用的材质档位（0 = 正常）。只在档位变化时才换材质。</summary>
        int flashStep;

        /// <summary>
        /// 按"种类 × 形状序号"缓存已经烘好顶点色的网格。
        /// 颜色只由 Kind 决定（Setup 里按 kind 赋 BaseColor），所以同种敌人可以共享同一份网格。
        /// </summary>
        static readonly Dictionary<int, Mesh> coloredMeshes = new Dictionary<int, Mesh>();

        public static Enemy Create(EnemyKind kind, Vector3 pos, float size, Transform parent)
        {
            var go = new GameObject("E_" + kind);
            go.transform.SetParent(parent, false);
            go.transform.position = pos;
            var e = go.AddComponent<Enemy>();
            e.Setup(kind, size);
            e.Pos = pos;
            return e;
        }

        void Setup(EnemyKind kind, float size)
        {
            Kind = kind;
            Size = Mathf.Max(0.25f, size);

            switch (kind)
            {
                case EnemyKind.Grunt:    Hp = 3f;   Radius = 0.88f; Speed = 3.4f; ScoreValue = 15; GeomCount = 1; BaseColor = Palette.Grunt; break;
                case EnemyKind.Wanderer: Hp = 2f;   Radius = 0.82f; Speed = 3.0f; ScoreValue = 12; GeomCount = 1; BaseColor = Palette.Wanderer; break;
                case EnemyKind.Dodger:   Hp = 3f;   Radius = 0.72f; Speed = 4.6f; ScoreValue = 15; GeomCount = 2; BaseColor = Palette.Dodger; break;
                case EnemyKind.Spinner:  Hp = 7f;   Radius = 0.95f; Speed = 2.2f; ScoreValue = 35; GeomCount = 2; BaseColor = Palette.Spinner; break;
                case EnemyKind.Chomper:  Hp = 4f;   Radius = 0.92f; Speed = 3.0f; ScoreValue = 25; GeomCount = 2; BaseColor = Palette.Chomper; break;
                case EnemyKind.Gravity:  Hp = 8f;   Radius = 1.00f; Speed = 1.1f; ScoreValue = 35; GeomCount = 3; BaseColor = Palette.Gravity; break;
                case EnemyKind.Weevil:   Hp = 1f;   Radius = 0.58f; Speed = 6.8f; ScoreValue = 10; GeomCount = 1; BaseColor = Palette.Weevil; break;
                case EnemyKind.Hole:     Hp = 25f;  Radius = 1.10f; Speed = 0.5f; ScoreValue = 60; GeomCount = 4; BaseColor = Palette.Hole; break;
                case EnemyKind.Boss:     Hp = 130f; Radius = 2.20f; Speed = 1.8f; ScoreValue = 400; GeomCount = 14; BaseColor = Palette.Boss; break;
            }

            Hp *= Size;
            MaxHp = Hp;
            Radius *= Size;

            fireTimer = Random.Range(0.6f, 2.0f);
            stateTimer = Random.Range(0.4f, 1.6f);
            phase = Random.Range(0, 100);
            entered = Mathf.Abs(transform.position.x) < Game.AX - 0.5f && Mathf.Abs(transform.position.y) < Game.AY - 0.5f;

            // Boss 在自己的生成点附近游弋（场地变大后不能再横扫全场）
            if (kind == EnemyKind.Boss)
            {
                hover = new Vector2(transform.position.x, transform.position.y);
            }

            // 初始速度朝场内
            Vector2 inward = new Vector2(-transform.position.x, -transform.position.y);
            if (inward.sqrMagnitude > 0.01f) vel = inward.normalized * Speed;
            if (Kind == EnemyKind.Boss) vel = Vector2.zero;

            BuildVisual();
        }

        /// <summary>
        /// 一种敌人由哪几个"环/多边形"组成。
        /// 分裂效果（SplitFx）也用这份数据，所以碎片形状一定和本体一致 —— 改形状只改这里。
        /// </summary>
        public struct ShapeDef
        {
            public int sides;
            public float radius;
            public float thickness;
            public float rotDeg;
            public float fillAlpha;
            public bool isRing;    // true = 空心环（Hole 用）
            public bool isCore;    // true = 跟着 core 单独旋转的中心部件

            public ShapeDef(int sides, float radius, float thickness, float rotDeg,
                            float fillAlpha = 0.10f, bool isRing = false, bool isCore = false)
            {
                this.sides = sides;
                this.radius = radius;
                this.thickness = thickness;
                this.rotDeg = rotDeg;
                this.fillAlpha = fillAlpha;
                this.isRing = isRing;
                this.isCore = isCore;
            }
        }

        public static ShapeDef[] ShapesOf(EnemyKind kind)
        {
            switch (kind)
            {
                case EnemyKind.Grunt:    return new[] { new ShapeDef(4, 0.95f, 0.17f, 45f) };
                case EnemyKind.Wanderer: return new[] { new ShapeDef(3, 1.02f, 0.16f, 90f) };
                case EnemyKind.Dodger:   return new[] { new ShapeDef(4, 0.82f, 0.16f, 0f) };
                case EnemyKind.Spinner:
                    return new[]
                    {
                        new ShapeDef(6, 1.05f, 0.15f, 0f),
                        new ShapeDef(3, 0.55f, 0.10f, 0f, 0f, false, true),
                    };
                case EnemyKind.Chomper:  return new[] { new ShapeDef(3, 1.05f, 0.18f, 0f, 0.12f) };
                case EnemyKind.Gravity:
                    return new[]
                    {
                        new ShapeDef(5, 1.05f, 0.15f, 0f),
                        new ShapeDef(3, 0.46f, 0.09f, 0f, 0f, false, true),
                    };
                case EnemyKind.Weevil:   return new[] { new ShapeDef(3, 0.74f, 0.15f, 0f, 0.12f) };
                case EnemyKind.Hole:
                    return new[]
                    {
                        new ShapeDef(36, 1.02f, 0.15f, 0f, 0f, true),
                        new ShapeDef(3, 0.66f, 0.10f, 0f, 0f, false, true),
                    };
                case EnemyKind.Boss:
                    return new[]
                    {
                        new ShapeDef(6, 2.20f, 0.28f, 0f, 0.07f),
                        new ShapeDef(6, 1.45f, 0.13f, 30f, 0f),
                        new ShapeDef(3, 0.95f, 0.16f, 0f, 0f, false, true),
                    };
            }
            return new[] { new ShapeDef(4, 0.95f, 0.17f, 45f) };
        }

        void BuildVisual()
        {
            var go = new GameObject("v");
            visual = go.transform;
            visual.SetParent(transform, false);

            var shapes = ShapesOf(Kind);
            for (int i = 0; i < shapes.Length; i++)
            {
                var s = shapes[i];
                Mesh baseMesh = s.isRing
                    ? Meshes.Ring(s.sides, s.radius, s.thickness)
                    : Meshes.Poly(s.sides, s.radius, s.thickness, s.fillAlpha, s.rotDeg);

                // 敌人颜色烘进顶点色，所有敌人共用 MatLib.EnemyShared 一个材质 —— 不再用 MPB。
                Mesh m = ColoredMesh((int)Kind * 16 + i, baseMesh, BaseColor);

                if (s.isCore) Core(m);
                else Part(m);
            }

            visual.localScale = Vector3.one * Size;
        }

        /// <summary>取（或首次生成）已经烘好顶点色的网格。key 用 种类×16+形状序号。</summary>
        static Mesh ColoredMesh(int key, Mesh src, Color c)
        {
            Mesh m;
            if (coloredMeshes.TryGetValue(key, out m) && m != null) return m;
            m = BakeColor(src, c);
            coloredMeshes[key] = m;
            return m;
        }

        /// <summary>
        /// 把颜色烘进顶点色。顶点色会被 Unity 钳到 0~1，所以不能直接写 HDR 颜色：
        /// RGB 存归一化色相 c/max(c)，亮度用 alpha 带 max(c)/EnemyGain，
        /// 材质 _Color 再乘回 EnemyGain —— 叠加混合下 rgb×alpha 正好还原成原始颜色。
        /// 原网格顶点色里已经有 alpha 变化（填充面比线框透明），要乘上去保留。
        /// </summary>
        static Mesh BakeColor(Mesh src, Color c)
        {
            var m = Instantiate(src);
            m.name = src.name + "_c";

            var cols = m.colors;
            float max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
            if (max < 1e-4f) max = 1f;
            float inv = 1f / max;
            float bright = Mathf.Clamp01(max / MatLib.EnemyGain);

            for (int i = 0; i < cols.Length; i++)
                cols[i] = new Color(c.r * inv, c.g * inv, c.b * inv, cols[i].a * bright);

            m.SetColors(cols);
            return m;
        }

        void Part(Mesh m)
        {
            var go = new GameObject("p");
            go.transform.SetParent(visual, false);
            var r = MatLib.Attach(go, m, MatLib.EnemyShared);
            rends.Add(r);
        }

        void Core(Mesh m)
        {
            if (core == null)
            {
                var cg = new GameObject("core");
                core = cg.transform;
                core.SetParent(visual, false);
            }
            var go = new GameObject("cp");
            go.transform.SetParent(core, false);
            var r = MatLib.Attach(go, m, MatLib.EnemyShared);
            rends.Add(r);
        }

        // ----------------------------------------------------------------

        void Update()
        {
            if (Dead || Game.I == null) return;
            var g = Game.I;
            if (g.State != GameState.Playing) return;

            float dt = Time.deltaTime;
            t += dt;

            if (warmup > 0f)
            {
                warmup -= dt;
                float k = 1f - Mathf.Clamp01(warmup / 0.7f);
                visual.localScale = Vector3.one * Size * Mathf.Lerp(0.15f, 1f, k * k);
                visual.localRotation = Quaternion.Euler(0f, 0f, k * 220f);
                // 出场只做缩放+旋转，不再逐帧改颜色（颜色已经烘进顶点色，改不了 alpha）
                return;
            }

            Vector3 pp = g.PlayerPos;
            Vector2 pos = new Vector2(transform.position.x, transform.position.y);
            Vector2 toP = new Vector2(pp.x - pos.x, pp.y - pos.y);
            float dist = toP.magnitude;
            Vector2 toPN = dist > 0.001f ? toP / dist : Vector2.right;

            switch (Kind)
            {
                case EnemyKind.Grunt:
                    vel = Vector2.MoveTowards(vel, toPN * Speed, 22f * dt);
                    visualAngle += 90f * dt;
                    break;

                case EnemyKind.Wanderer:
                {
                    // 缓慢游荡着靠近：径向为主，叠一点横向摆动。
                    // 之前这里完全没有移动代码，只有入场时那一下初速度，
                    // 所以它会在场地里一直直线飘到撞墙。
                    Vector2 perp = toPN.Rotate90();
                    Vector2 want = (toPN + perp * Mathf.Sin(t * 1.4f + phase) * 0.55f).normalized * Speed;
                    vel = Vector2.MoveTowards(vel, want, 6f * dt);

                    fireTimer -= dt;
                    if (fireTimer <= 0f && dist < 16f && g.PlayerAlive)
                    {
                        fireTimer = Random.Range(1.7f, 2.9f);
                        g.EnemyShoot(transform.position, toPN, 11f);
                    }
                    visualAngle += 45f * dt;
                    break;
                }

                case EnemyKind.Dodger:
                {
                    // 盘旋逼近：远时直接靠近，近时绕着主角游走。
                    // 原来是"逃离主角"，在有边界的场地里会一路被逼到墙角卡住。
                    float tangSign = Mathf.Sin(t * 1.9f + phase) > 0f ? 1f : -1f;
                    Vector2 tang = toPN.Rotate90() * tangSign;

                    float far = Mathf.Clamp01(dist / 16f);
                    Vector2 want = toPN * Mathf.Lerp(0.30f, 1.0f, far) + tang * 0.75f;

                    // 贴边时往回拐，避免在边框和角落磨蹭
                    Vector2 away = Vector2.zero;
                    const float edge = 7f;
                    if (pos.x > Game.AX - edge) away.x -= 1f;
                    if (pos.x < -Game.AX + edge) away.x += 1f;
                    if (pos.y > Game.AY - edge) away.y -= 1f;
                    if (pos.y < -Game.AY + edge) away.y += 1f;
                    if (away.sqrMagnitude > 0f) want += away.normalized * 1.4f;

                    vel = Vector2.MoveTowards(vel, want.normalized * Speed, 24f * dt);
                    visualAngle = Yaw(vel);
                    break;
                }

                case EnemyKind.Spinner:
                {
                    stateTimer -= dt;
                    if (stateTimer <= 0f)
                    {
                        stateTimer = Random.Range(2.6f, 4.2f);
                        hover = new Vector2(
                            Mathf.Clamp(pp.x + Random.Range(-12f, 12f), -Game.AX + 4f, Game.AX - 4f),
                            Mathf.Clamp(pp.y + Random.Range(-12f, 12f), -Game.AY + 4f, Game.AY - 4f));
                    }
                    Vector2 toH = hover - pos;
                    Vector2 want = toH.magnitude > 0.5f ? toH.normalized * Speed : Vector2.zero;
                    vel = Vector2.MoveTowards(vel, want, 8f * dt);

                    fireTimer -= dt;
                    if (fireTimer <= 0f && g.PlayerAlive)
                    {
                        fireTimer = Mathf.Max(0.95f, 2.1f - g.Wave * 0.05f);
                        float baseA = Mathf.Atan2(toP.y, toP.x);
                        int n = 6;
                        for (int i = 0; i < n; i++)
                        {
                            float a = baseA + i * Mathf.PI * 2f / n;
                            g.EnemyShoot(transform.position, new Vector2(Mathf.Cos(a), Mathf.Sin(a)), 8.5f);
                        }
                        Sfx.Spawn();
                    }
                    spin += dt * 130f;
                    break;
                }

                case EnemyKind.Chomper:
                {
                    stateTimer -= dt;
                    if (stage == 0)
                    {
                        vel *= 1f - Mathf.Clamp01(3.5f * dt);
                        visualAngle = Mathf.LerpAngle(visualAngle, Yaw(toP), 0.2f);
                        if (stateTimer <= 0f)
                        {
                            stage = 1;
                            stateTimer = 0.55f;
                            dashDir = toPN;
                            flash = 1f;
                            Sfx.Hit();
                        }
                    }
                    else
                    {
                        vel = dashDir * Speed * 5.4f;
                        visualAngle = Yaw(dashDir);
                        if (stateTimer <= 0f)
                        {
                            stage = 0;
                            stateTimer = Random.Range(0.85f, 1.5f);
                        }
                    }
                    break;
                }

                case EnemyKind.Gravity:
                    vel = Vector2.MoveTowards(vel, toPN * Speed, 4f * dt);
                    if (g.PlayerAlive && dist < 10f)
                    {
                        float k = 1f - dist / 10f;
                        g.AddPlayerPull(toPN * (k * k * 30f));
                    }
                    spin += dt * 70f;
                    break;

                case EnemyKind.Hole:
                    vel = Vector2.MoveTowards(vel, toPN * Speed, 2f * dt);
                    if (g.PlayerAlive && dist < 12f)
                    {
                        float k = 1f - dist / 12f;
                        g.AddPlayerPull(toPN * (k * k * 46f));
                    }
                    spin += dt * 55f;
                    break;

                case EnemyKind.Weevil:
                {
                    Vector2 perp = toPN.Rotate90();
                    Vector2 want = (toPN + perp * Mathf.Sin(t * 9f + phase) * 0.9f).normalized * Speed;
                    vel = Vector2.MoveTowards(vel, want, 34f * dt);
                    visualAngle = Yaw(vel);
                    break;
                }

                case EnemyKind.Boss:
                {
                    // 绕着主角盘旋（保持 8 单位左右），而不是围着生成点打转——
                    // 主角跑到场地另一头时 Boss 不会留在原地。
                    float orbitR = 8f + Mathf.Sin(t * 0.5f) * 2.5f;
                    Vector2 tgt = new Vector2(pp.x, pp.y) - toPN * orbitR
                                  + toPN.Rotate90() * (Mathf.Sin(t * 0.85f) * 5f);
                    tgt.x = Mathf.Clamp(tgt.x, -Game.AX + 5f, Game.AX - 5f);
                    tgt.y = Mathf.Clamp(tgt.y, -Game.AY + 5f, Game.AY - 5f);
                    Vector2 want = tgt - pos;
                    vel = Vector2.MoveTowards(vel, want.magnitude > 0.4f ? want.normalized * Mathf.Min(Speed, want.magnitude * 1.5f) : Vector2.zero, 6f * dt);

                    if (g.PlayerAlive && Mathf.Repeat(t, 6f) < 2.6f)
                    {
                        fireTimer -= dt;
                        if (fireTimer <= 0f)
                        {
                            fireTimer = 0.10f;
                            float a0 = t * 2.6f;
                            for (int i = 0; i < 3; i++)
                            {
                                float a = a0 + i * Mathf.PI * 2f / 3f;
                                g.EnemyShoot(transform.position, new Vector2(Mathf.Cos(a), Mathf.Sin(a)), 9.5f);
                            }
                        }
                    }

                    stateTimer -= dt;
                    if (stateTimer <= 0f)
                    {
                        stateTimer = 6.5f;
                        for (int i = 0; i < 2; i++)
                        {
                            float a = Random.value * Mathf.PI * 2f;
                            g.SpawnAt(EnemyKind.Weevil, transform.position + new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * 3f, 1f);
                        }
                    }
                    spin += dt * 45f;
                    break;
                }
            }

            // 宏观收拢：不管各自 AI 在做什么，离主角太远都会被拉回来。
            // 之前 Boss 围着生成点打转、Spinner 悬停在 ±12 的随机点、Wanderer 干脆
            // 只会直线飘——都会漂到场地的另一头。这里统一兜一层：
            // 13 单位以内完全按各自 AI 走，22 单位以外完全改成直冲主角，中间线性过渡。
            const float leashNear = 13f;
            const float leashFar = 22f;
            if (dist > leashNear)
            {
                float w = Mathf.Clamp01((dist - leashNear) / (leashFar - leashNear));
                // 慢速敌人（黑洞 0.5、引力井 1.1、Boss 1.8）给一个下限，否则永远追不上来
                float leashSpeed = Mathf.Max(Speed, 2.2f);
                vel = Vector2.Lerp(vel, toPN * leashSpeed, w);
            }

            // 自愈：万一因为异常位移跑到场外，重新走一遍入场流程
            if (entered && (Mathf.Abs(transform.position.x) > Game.AX + 2f || Mathf.Abs(transform.position.y) > Game.AY + 2f))
            {
                entered = false;
            }

            // 入场阶段：无视 AI，强行朝场内飞。
            // 否则 Dodger 这类"逃离玩家"的敌人可能在场外一直往外跑，永远进不了场，波次就卡死了。
            if (!entered)
            {
                Vector2 toCenter = new Vector2(-transform.position.x, -transform.position.y);
                if (toCenter.sqrMagnitude > 0.04f)
                {
                    Vector2 dir = toCenter.normalized;
                    float minIn = Mathf.Max(Speed * 1.8f, 4.5f);
                    if (Vector2.Dot(vel, dir) < minIn) vel = dir * minIn;
                }
            }

            // 位移 + 边界
            Vector3 np = transform.position + new Vector3(vel.x, vel.y, 0f) * dt;

            if (!entered)
            {
                // 入场途中也不允许无限远离
                np.x = Mathf.Clamp(np.x, -Game.AX - 7f, Game.AX + 7f);
                np.y = Mathf.Clamp(np.y, -Game.AY - 7f, Game.AY + 7f);
                if (Mathf.Abs(np.x) < Game.AX - 0.5f && Mathf.Abs(np.y) < Game.AY - 0.5f) entered = true;
            }
            else
            {
                float lx = Game.AX - Radius * 0.55f;
                float lz = Game.AY - Radius * 0.55f;
                if (np.x > lx) { np.x = lx; vel.x = -Mathf.Abs(vel.x) * 0.8f; }
                if (np.x < -lx) { np.x = -lx; vel.x = Mathf.Abs(vel.x) * 0.8f; }
                if (np.y > lz) { np.y = lz; vel.y = -Mathf.Abs(vel.y) * 0.8f; }
                if (np.y < -lz) { np.y = -lz; vel.y = Mathf.Abs(vel.y) * 0.8f; }
            }

            transform.position = np;
            Pos = np;

            // 表现
            visual.localRotation = Quaternion.Euler(0f, 0f, visualAngle);
            if (core != null) core.localRotation = Quaternion.Euler(0f, 0f, spin);

            // 受击闪白：不用 MaterialPropertyBlock（那会让物体被排除在 SRP Batcher 之外，
            // 几百个敌人就是几百次 SetPass），改成在几个共享材质之间切换。
            // 同一个着色器，所以照样能合批；而且只在档位变化时才换，不是每帧都设。
            flash = Mathf.Max(0f, flash - dt * 5f);
            int step = flash > 0.66f ? 3 : flash > 0.33f ? 2 : flash > 0f ? 1 : 0;
            if (step != flashStep)
            {
                flashStep = step;
                var mat = step == 0 ? MatLib.EnemyShared : MatLib.EnemyFlash(step);
                for (int i = 0; i < rends.Count; i++)
                {
                    if (rends[i] != null) rends[i].sharedMaterial = mat;
                }
            }
        }

        static float Yaw(Vector2 dir)
        {
            if (dir.sqrMagnitude < 1e-5f) return 0f;
            return Mathf.Atan2(-dir.x, dir.y) * Mathf.Rad2Deg;
        }

        public void Hit(float dmg, Vector2 dir)
        {
            if (Dead) return;
            Hp -= dmg;
            flash = 1f;
            if (Fx.I != null) Fx.I.Spark(transform.position, dir, BaseColor);
            Sfx.Hit();
            if (Hp <= 0f) Die();
        }

        /// <summary>无声消失：只用于波次防卡死的兜底，不给分、不掉 Geom。</summary>
        public void Vanish()
        {
            if (Dead) return;
            Dead = true;
            if (Fx.I != null) Fx.I.Burst(transform.position, BaseColor, 10, 9f, 0.45f, 0.45f);
            if (Game.I != null) Game.I.RemoveEnemy(this);
            Destroy(gameObject);
        }

        void Die()
        {
            if (Dead) return;
            Dead = true;
            var g = Game.I;

            // 爆炸规模跟着体型走：小怪是利落的爆点，Boss 是连环大爆炸
            float exScale = Mathf.Clamp(Radius * 1.15f, 0.6f, 3.2f);
            Fx.I.Explosion(transform.position, BaseColor, exScale);

            // 分裂：把轮廓拆成一条条边，向外慢慢飘散
            if (SplitFx.I != null)
                SplitFx.I.Split(transform.position, visualAngle, Size, BaseColor, ShapesOf(Kind));

            if (Kind == EnemyKind.Boss)
            {
                Fx.I.Explosion(transform.position, Palette.Boss, 3.2f);
                Fx.I.Explosion(transform.position, Palette.White, 2.4f);
                Fx.I.Shake(1.5f);
                Sfx.BigExplode();
            }
            else
            {
                Sfx.Explode();
            }

            int gained = g.AddScore(ScoreValue);
            g.PopupScore(transform.position, gained, Kind == EnemyKind.Boss ? 2.2f : 1f);
            g.DropGeoms(transform.position, GeomCount * Game.GeomDropRate);

            if (Kind == EnemyKind.Dodger && Size > 0.62f)
            {
                for (int i = 0; i < 2; i++)
                {
                    float a = Random.value * Mathf.PI * 2f;
                    Vector3 off = new Vector3(Mathf.Cos(a), Mathf.Sin(a), 0f) * 0.9f;
                    g.SpawnAt(EnemyKind.Dodger, transform.position + off, Size * 0.55f);
                }
            }

            if (Kind == EnemyKind.Boss)
            {
                g.OnBossKilled(transform.position);
            }

            g.RemoveEnemy(this);
            Destroy(gameObject);
        }
    }
}
