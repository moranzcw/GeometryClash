using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace GeoClash
{
    public enum GameState { Title, Playing, Paused, GameOver }

    /// <summary>
    /// 游戏主控制器，也是**整个游戏的唯一入口**。
    ///
    /// 场景里只放这一个物体（GeoClash 物体上挂着本组件），
    /// 相机、网格地面、玩家、UI、后处理全部在 Awake() 里用代码创建，
    /// 所以场景视图里看不到别的东西是正常的。
    ///
    /// 如果直接在其他场景（例如 SampleScene）里按 Play，
    /// Boot.cs 的 RuntimeInitializeOnLoadMethod 会自动补建这个物体。
    /// </summary>
    [DisallowMultipleComponent]
    public class Game : MonoBehaviour
    {
        public static Game I;

        // 场地半宽/半高。取值保证 2*AX/1.4 与 2*AY/1.4 都是整数，
        // 弹簧网格才能严格铺满场地边界（否则边缘会差半格，露出缝隙）。
        public const float AX = 41.3f;   // 82.6 宽 = 59 格 × 1.4
        public const float AY = 24.5f;   // 49.0 高 = 35 格 × 1.4

        [Header("Runtime State (play mode only)")]
        public GameState State = GameState.Title;
        public int Score, Lives, Bombs, Wave, Best;
        public float Multiplier = 1f;

        /// <summary>
        /// 无限生命：阵亡后照常走复活流程（黑洞坍缩 + 无敌环），但不扣命、也不会进 GameOver。
        /// 关掉就恢复成原来的 3 条命。
        /// </summary>
        public bool InfiniteLives = true;

        [Header("Runtime References (assigned in Awake)")]
        public Camera Cam;
        public Vector3 CamBase;
        public Transform World;
        public Player Player;
        public Hud Hud;

        public readonly List<Enemy> Enemies = new List<Enemy>();
        public readonly List<Bullet> PlayerBullets = new List<Bullet>();
        public readonly List<Bullet> EnemyBullets = new List<Bullet>();
        public readonly List<Geom> Geoms = new List<Geom>();

        Pool<Bullet> bulletPool;
        Pool<Geom> geomPool;

        float lastAspect = -1f;
        float respawnTimer;
        /// <summary>主角阵亡的位置，复活时就在这里原地重生。</summary>
        Vector3 deathPos;

        // 相机偏移强度：主角在场地最左/最右时，相机额外往同方向偏这么多；
        // 主角在场地中央时偏移为 0，中间线性过渡。
        const float SwayX = 8.0f;
        const float SwayY = 6.5f;

        // 推拉呼吸：相机沿自身朝向靠近/远离聚焦点，纯缩放，不产生任何旋转
        const float DollyAmount = 0.03f;
        float camDist = 33f;
        Vector3 camFocus;
        bool camInit;
        float viewHalfW = 31f, viewHalfH = 17.5f;
        float gameOverDelay;
        bool waveActive;
        float waveBreak;
        float outsideTimer;
        float multGrace;
        float bossHp = -1f, bossHpMax = 1f;
        float ambientTimer;

        struct SpawnReq
        {
            public EnemyKind Kind;
            public Vector3 Pos;
            public float Delay;
            public float Size;

            /// <summary>
            /// true = 位置在**发放那一刻**才算（围绕主角当前位置找一个远处的点）。
            /// 均匀刷怪必须用这个：一波要铺开几十秒，预先算好的坐标会落在
            /// 主角几十秒前站的地方，可能直接砸在脸上。
            /// </summary>
            public bool Live;
        }

        readonly List<SpawnReq> pending = new List<SpawnReq>();

        static readonly EnemyKind[] Kinds =
        {
            EnemyKind.Grunt, EnemyKind.Wanderer, EnemyKind.Dodger, EnemyKind.Spinner,
            EnemyKind.Chomper, EnemyKind.Gravity, EnemyKind.Weevil, EnemyKind.Hole
        };
        static readonly int[] Weights = { 4, 3, 2, 2, 2, 1, 3, 1 };
        // 顺序和 Kinds 一一对应：Grunt, Wanderer, Dodger, Spinner, Chomper, Gravity, Weevil, Hole
        // 引力井第 3 波、黑洞第 5 波就登场（原先是 5 和 8，太靠后，很多人根本见不到）
        static readonly int[] UnlockWave = { 1, 1, 2, 3, 4, 3, 6, 5 };
        static readonly int[] Cost = { 2, 2, 3, 4, 3, 5, 2, 6 };

        // ------------------------------------------------------------ 构建

        void Awake()
        {
            I = this;
            Application.targetFrameRate = 120;

            MatLib.Reset();
            Meshes.ClearCache();

            World = new GameObject("World").transform;
            World.SetParent(transform, false);

            BuildCamera();
            BuildArena();

            // 场地后方的 3D 点阵：相机平移/推拉时靠视差读出纵深
            var depthGo = new GameObject("DepthField");
            depthGo.transform.SetParent(transform, false);
            depthGo.AddComponent<DepthField>();

            var fxGo = new GameObject("Fx");
            fxGo.transform.SetParent(transform, false);
            var fx = fxGo.AddComponent<Fx>();
            fx.SetCamera(Cam);

            // 敌人死亡时的"轮廓分裂"碎片
            var splitGo = new GameObject("SplitFx");
            splitGo.transform.SetParent(transform, false);
            splitGo.AddComponent<SplitFx>();

            Sfx.Init(transform);

            var playerGo = new GameObject("Player");
            playerGo.transform.SetParent(World, false);
            Player = playerGo.AddComponent<Player>();
            Player.Init();
            // 开机这次只是初始化状态，不要放诞生特效
            Player.Respawn(Vector3.zero, false);
            Player.Alive = false;
            Player.SetVisible(false);

            bulletPool = new Pool<Bullet>(() => Bullet.Create(World), World, 120);
            geomPool = new Pool<Geom>(() => Geom.Create(World), World, 40);

            var hudGo = new GameObject("Hud");
            hudGo.transform.SetParent(transform, false);
            Hud = hudGo.AddComponent<Hud>();

            BuildPostFx();

            // 准星光标（敌人多的时候系统箭头很容易看丢）
            Crosshair.Apply();

            Best = PlayerPrefs.GetInt("geoclash_best", 0);
            State = GameState.Title;
        }

        void BuildCamera()
        {
            var go = new GameObject("MainCamera");
            go.tag = "MainCamera";
            go.transform.SetParent(transform, false);
            Cam = go.AddComponent<Camera>();
            Cam.clearFlags = CameraClearFlags.SolidColor;
            Cam.backgroundColor = Palette.Bg;
            Cam.orthographic = false;
            Cam.fieldOfView = 45f;
            Cam.nearClipPlane = 0.3f;
            Cam.farClipPlane = 400f;
            Cam.allowHDR = true;
            Cam.allowMSAA = false;
            go.AddComponent<AudioListener>();

            try
            {
                var data = Cam.GetUniversalAdditionalCameraData();
                data.renderPostProcessing = true;
                data.antialiasing = AntialiasingMode.SubpixelMorphologicalAntiAliasing;
                data.antialiasingQuality = AntialiasingQuality.High;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[GeoClash] URP 相机数据初始化失败: " + e.Message);
            }

            UpdateFraming();
        }

        void UpdateFraming()
        {
            if (Cam == null) return;
            lastAspect = Cam.aspect;

            // 游戏平面立在 z=0 上，相机在 -Z 方向正对着它。
            // 此时可见范围就是简单的透视投影：halfH = dist*tan(fov/2)，halfW = halfH*aspect。
            // 1.4 倍视野（原来 19 × 12.5，视野偏小、看不清敌群）
            const float targetHalfW = 26.6f;
            const float targetHalfH = 17.5f;

            float halfTan = Mathf.Tan(Cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float distH = targetHalfH / halfTan;
            float distW = targetHalfW / (halfTan * Mathf.Max(0.35f, Cam.aspect));
            camDist = Mathf.Max(distH, distW);

            // 实际可见范围（用于跟随夹紧）
            viewHalfH = halfTan * camDist;
            viewHalfW = viewHalfH * Cam.aspect;

            ApplyCamera();
        }

        /// <summary>
        /// 稳定器式相机：相机站在平面正前方（-Z），姿态永远由
        /// 「保持水平 + 看向聚焦点」自动决定。
        ///
        /// 偏移量由**主角在场地中的相对位置**驱动：
        /// 主角靠左 → 相机也往左偏；靠右 → 往右偏；上下同理。
        /// 因为平面是立起来的，偏移会让视线与平面法线产生夹角，
        /// 画面呈现梯形透视而不会整体旋转。
        /// </summary>
        void ApplyCamera()
        {
            float t = Time.time;

            // 推拉呼吸：只改变距离，不改变方向
            float dist = camDist * (1f + Mathf.Sin(t * 0.13f) * DollyAmount);

            // 主角在场地中的归一化位置（-1 = 最左/最下，+1 = 最右/最上）
            float nx = Mathf.Clamp(camFocus.x / AX, -1f, 1f);
            float ny = Mathf.Clamp(camFocus.y / AY, -1f, 1f);

            Vector3 camPos = new Vector3(
                camFocus.x + nx * SwayX,
                camFocus.y + ny * SwayY,
                -dist);

            CamBase = camPos;

            if (Cam != null)
            {
                Cam.transform.position = camPos;
                Cam.transform.rotation = Quaternion.LookRotation(camFocus - camPos, Vector3.up);
            }
        }

        /// <summary>镜头跟随主角：聚焦点夹在场地内留一点边距，场地外的部分露出黑底（和参考图一致）。</summary>
        void UpdateCamera(float dt)
        {
            Vector3 target = PlayerPos;

            // 前瞻：镜头略微偏向移动方向，强化速度感
            if (Player != null && Player.Alive)
            {
                target += new Vector3(Player.Velocity.x, Player.Velocity.y, 0f) * 0.12f;
            }

            // 聚焦点夹紧：视野最多越出场地边缘 12 个单位。
            // 注意视野变大后（62×35）几乎和场地（63×37.8）一样大，留给"跟随"的余量很小——
            // 如果强行让主角永远居中，镜头必须整块移出场地，反而会丢失边框。
            const float slack = 12f;
            float minX = -AX - slack + viewHalfW;
            float maxX = AX + slack - viewHalfW;
            float minY = -AY - slack + viewHalfH;
            float maxY = AY + slack - viewHalfH;

            if (minX > maxX) { float m = (minX + maxX) * 0.5f; minX = m; maxX = m; }
            if (minY > maxY) { float m = (minY + maxY) * 0.5f; minY = m; maxY = m; }

            target.x = Mathf.Clamp(target.x, minX, maxX);
            target.y = Mathf.Clamp(target.y, minY, maxY);
            target.z = 0f;

            if (!camInit)
            {
                camFocus = target;
                camInit = true;
            }
            else
            {
                camFocus = Vector3.Lerp(camFocus, target, 1f - Mathf.Exp(-9f * dt));
            }

            ApplyCamera();
        }

        void BuildArena()
        {
            // 弹簧质点网格：整张网格就是场地地面
            var gridGo = new GameObject("SpringGrid");
            gridGo.transform.SetParent(World, false);
            gridGo.AddComponent<SpringGrid>();

            var borderGo = new GameObject("Border");
            borderGo.transform.SetParent(World, false);
            borderGo.transform.position = new Vector3(0f, 0f, 0.05f);
            MatLib.Attach(borderGo, Meshes.Border(AX, AY, 0.30f), MatLib.Neon(Palette.Border));
        }

        void BuildPostFx()
        {
            try
            {
                var go = new GameObject("PostFx");
                go.transform.SetParent(transform, false);
                var vol = go.AddComponent<Volume>();
                vol.isGlobal = true;
                vol.priority = 10f;

                var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                vol.sharedProfile = profile;

                var bloom = profile.Add<Bloom>(true);
                bloom.threshold.Override(0.72f);
                bloom.intensity.Override(0.85f);
                bloom.scatter.Override(0.72f);
                bloom.tint.Override(new Color(0.55f, 0.85f, 1f));
                bloom.highQualityFiltering.Override(true);

                var vig = profile.Add<Vignette>(true);
                vig.intensity.Override(0.32f);
                vig.smoothness.Override(0.55f);
                vig.color.Override(Color.black);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("[GeoClash] 后处理初始化失败: " + e.Message);
            }
        }

        // ------------------------------------------------------------ 对外接口

        public bool PlayerAlive { get { return Player != null && Player.Alive; } }

        public Vector3 PlayerPos { get { return Player != null ? Player.transform.position : Vector3.zero; } }

        public float BossHp01
        {
            get { return bossHp < 0f ? -1f : Mathf.Clamp01(bossHp / Mathf.Max(1f, bossHpMax)); }
        }

        /// <summary>加分并返回实际增加的分值（已乘倍率），供飘字使用。</summary>
        public int AddScore(int baseValue)
        {
            int add = Mathf.RoundToInt(baseValue * Multiplier);
            Score += add;
            return add;
        }

        /// <summary>在指定位置弹出一个得分飘字。</summary>
        public void PopupScore(Vector3 pos, int value, float scale = 1f)
        {
            if (Hud != null) Hud.ScorePopup(pos, value, scale);
        }

        public void CollectGeom(Vector3 pos)
        {
            Multiplier = Mathf.Min(30f, Multiplier + GeomMultiplierGain);
            multGrace = 2.5f;
            Score += Mathf.RoundToInt(6f * Multiplier);
            Sfx.Geom();
            Fx.I.Burst(pos, Palette.Geom, 4, 5f, 0.28f, 0.3f);
        }

        /// <summary>从池里取对象前先清掉已死的引用，避免同一个对象在列表里出现两次。</summary>
        static void CompactBullets(List<Bullet> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] == null || !list[i].Alive) list.RemoveAt(i);
            }
        }

        static void CompactGeoms(List<Geom> list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] == null || !list[i].Alive) list.RemoveAt(i);
            }
        }

        /// <summary>掉落物产出比例：1 = 每个敌人的 GeomCount 全掉，0.2 = 只掉 20%。</summary>
        public const float GeomDropRate = 0.2f;

        /// <summary>每个 Geom 增加的倍率。掉落量降低后单颗价值提高，维持整体成长速度。</summary>
        public const float GeomMultiplierGain = 0.6f;

        /// <summary>按小数数量掉落：整数部分必掉，小数部分按概率补一个。</summary>
        public void DropGeoms(Vector3 pos, float count)
        {
            int n = Mathf.FloorToInt(count);
            if (Random.value < count - n) n++;
            if (n <= 0) return;

            CompactGeoms(Geoms);
            for (int i = 0; i < n; i++)
            {
                var g = geomPool.Get();
                if (g == null) return;
                g.Pool = geomPool;
                g.Launch(pos);
                Geoms.Add(g);
            }
        }

        /// <summary>玩家子弹的基础速度（不含自身速度叠加）。</summary>
        public const float BulletSpeed = 48f;
        /// <summary>叠加自身速度后允许的最低子弹速度，防止倒退射击时子弹慢到几乎不动。</summary>
        public const float BulletSpeedMin = 12f;

        /// <summary>
        /// 玩家开火。inheritSpeed 是主角速度在射击方向上的分量：
        /// 向前冲时为正（子弹更快），边退边打时为负（子弹更慢）。
        /// </summary>
        public void PlayerShoot(Vector3 pos, Vector2 dir, float inheritSpeed)
        {
            CompactBullets(PlayerBullets);
            var b = bulletPool.Get();
            if (b == null) return;
            b.Pool = bulletPool;

            float speed = Mathf.Max(BulletSpeedMin, BulletSpeed + inheritSpeed);
            b.Launch(pos, dir * speed, true, Palette.Bullet);
            PlayerBullets.Add(b);
        }

        public void EnemyShoot(Vector3 pos, Vector2 dir, float speed)
        {
            if (EnemyBullets.Count > 320) return;
            CompactBullets(EnemyBullets);
            var b = bulletPool.Get();
            if (b == null) return;
            b.Pool = bulletPool;
            b.Launch(pos, dir * speed, false, Palette.EnemyBullet);
            EnemyBullets.Add(b);
        }

        public void SpawnAt(EnemyKind kind, Vector3 pos, float size)
        {
            var e = Enemy.Create(kind, pos, size, World);
            Enemies.Add(e);
            Fx.I.Shockwave(pos, e.BaseColor, 2.6f, 0.3f);
        }

        public void RemoveEnemy(Enemy e)
        {
            Enemies.Remove(e);
        }

        public void OnBossKilled(Vector3 pos)
        {
            Fx.I.Shockwave(pos, Palette.Boss, 26f, 1.1f);
            Fx.I.Shockwave(pos, Palette.White, 16f, 0.7f);
            Fx.I.Burst(pos, Palette.Boss, 80, 24f, 0.8f, 1.1f);
            Fx.I.Shake(1.4f);
            for (int i = EnemyBullets.Count - 1; i >= 0; i--)
            {
                if (EnemyBullets[i] != null) EnemyBullets[i].Despawn();
            }
            Bombs = Mathf.Min(5, Bombs + 2);
            int bonus = Mathf.RoundToInt(1000f * Multiplier);
            Score += bonus;
            PopupScore(pos, bonus, 2.6f);
            Hud.Announce("BOSS DOWN", 2f);
        }

        // ------------------------------------------------------------ 波次

        /// <summary>在场地内部随机取一个刷新点：围绕主角 14~34 单位的环带内，保证都落在框里。</summary>
        /// <summary>
        /// 在场地内取一个刷新点：离主角至少 MinSpawnDist，且留出边界余量。
        /// 24 次随机都不满足时，退回"已找到的候选里离主角最远的那个"，
        /// 再不行才用对角角落——总之绝不会刷在主角身上。
        /// </summary>
        Vector3 SpawnPoint()
        {
            const float margin = 4f;
            const float minDist = 15f;
            float minDistSqr = minDist * minDist;

            Vector3 pp = PlayerPos;
            Vector3 best = Vector3.zero;
            float bestD = -1f;

            for (int i = 0; i < 24; i++)
            {
                float ang = Random.value * Mathf.PI * 2f;
                float dist = Random.Range(minDist, 30f);
                Vector3 p = new Vector3(pp.x + Mathf.Cos(ang) * dist, pp.y + Mathf.Sin(ang) * dist, 0f);

                if (Mathf.Abs(p.x) > AX - margin || Mathf.Abs(p.y) > AY - margin) continue;

                float d2 = (p - pp).sqrMagnitude;
                if (d2 >= minDistSqr) return p;
                if (d2 > bestD) { bestD = d2; best = p; }
            }

            if (bestD >= 0f) return best;

            // 主角把整个环带都挡住了（贴角落时才会发生）：刷到对角去
            return new Vector3(
                (pp.x >= 0f ? -1f : 1f) * (AX - margin - 2f),
                (pp.y >= 0f ? -1f : 1f) * (AY - margin - 2f),
                0f);
        }

        EnemyKind? PickKind(int wave, int budget)
        {
            int total = 0;
            for (int i = 0; i < Kinds.Length; i++)
            {
                if (wave >= UnlockWave[i] && Cost[i] <= budget) total += Weights[i];
            }
            if (total <= 0) return null;

            int r = Random.Range(0, total);
            for (int i = 0; i < Kinds.Length; i++)
            {
                if (wave < UnlockWave[i] || Cost[i] > budget) continue;
                r -= Weights[i];
                if (r < 0) return Kinds[i];
            }
            return null;
        }

        void StartWave(int n)
        {
            Wave = n;
            waveActive = true;
            pending.Clear();

            bool boss = (n % 5 == 0);
            int budget = (7 + n * 5) * 15;   // 敌人密度约为最初的 15 倍

            if (boss)
            {
                pending.Add(new SpawnReq { Kind = EnemyKind.Boss, Delay = 1.2f, Size = 1f, Live = true });
                budget = (4 + n * 2) * 9;
            }

            var counts = new Dictionary<EnemyKind, int>();
            int guard = 0;
            while (budget >= 2 && guard++ < 600)
            {
                var k = PickKind(n, budget);
                if (k == null) break;
                int c = Cost[(int)k.Value];
                if (c > budget) break;
                budget -= c;
                if (counts.ContainsKey(k.Value)) counts[k.Value]++;
                else counts[k.Value] = 1;
            }

            // ---- 均匀刷怪 ----
            // 以前是"按种类分组、每组 5~10 只一口气倒出来、隔 0.45 秒再倒一组"，
            // 所以一波敌人几秒内就全在场上了。
            // 现在先把整波要出的敌人摊平成一个列表、打散顺序，
            // 再按固定间隔逐只发放 —— 敌人会从整波开头一路均匀滴到结尾。
            var roster = new List<EnemyKind>();
            foreach (var kv in counts)
            {
                for (int i = 0; i < kv.Value; i++) roster.Add(kv.Key);
            }
            for (int i = roster.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                var tmp = roster[i];
                roster[i] = roster[j];
                roster[j] = tmp;
            }

            // ---- 分组成批刷怪 ----
            // 一波的敌人先打散、再切成若干组：
            //   组内几乎同时出现（形成一次"爆发"，能看出是同一批来的）
            //   组与组之间留一段基础间隔（让玩家有喘息和清场的时间）
            // 以前组间隔只有 0.45 秒，一波敌人十几秒就全涌出来了，几乎连成一片。
            const float inGroupGap = 0.06f;      // 组内个体间隔
            const float groupGap = 1.35f;        // 组与组之间的基础间隔
            const float groupGapJitter = 0.35f;  // 间隔抖动，避免像节拍器

            // 每组 9 只左右，组数夹在 6~18 之间：
            // 早期一波 10 组左右，后期敌人多就变成 18 组、每组更多，但节奏不变
            int groupCount = Mathf.Clamp(Mathf.RoundToInt(roster.Count / 9f), 6, 18);
            int groupSize = Mathf.CeilToInt(roster.Count / (float)groupCount);

            float t = boss ? 3.0f : 0.8f;   // 有 BOSS 时先让 BOSS 出场 1.2 秒
            int idx = 0;
            for (int gi = 0; gi < groupCount && idx < roster.Count; gi++)
            {
                int take = Mathf.Min(groupSize, roster.Count - idx);
                for (int j = 0; j < take; j++, idx++)
                {
                    pending.Add(new SpawnReq
                    {
                        Kind = roster[idx],
                        Delay = t + j * inGroupGap,
                        Size = 1f,
                        Live = true,
                    });
                }
                t += take * inGroupGap + groupGap + Random.Range(-groupGapJitter, groupGapJitter);
            }

            Hud.Announce("WAVE " + n, 1.8f);
            Sfx.Wave();
        }

        void UpdateSpawns(float dt)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var s = pending[i];
                s.Delay -= dt;
                if (s.Delay <= 0f)
                {
                    // Live 的位置在这里才算：围绕主角"此刻"的位置找远处的点
                    SpawnAt(s.Kind, s.Live ? SpawnPoint() : s.Pos, s.Size);
                    Sfx.Spawn();
                    pending.RemoveAt(i);
                }
                else
                {
                    pending[i] = s;
                }
            }
        }

        void UpdateWave(float dt)
        {
            if (waveActive)
            {
                // 兜底：敌人全部滞留在场外且持续数秒，说明有敌人卡住了，直接清除，避免波次永远不结束
                if (pending.Count == 0 && Enemies.Count > 0)
                {
                    bool allOutside = true;
                    for (int i = 0; i < Enemies.Count; i++)
                    {
                        var e = Enemies[i];
                        if (e == null) continue;
                        Vector3 p = e.transform.position;
                        if (Mathf.Abs(p.x) < AX + 1.5f && Mathf.Abs(p.y) < AY + 1.5f)
                        {
                            allOutside = false;
                            break;
                        }
                    }

                    outsideTimer = allOutside ? outsideTimer + dt : 0f;

                    if (outsideTimer > 6f)
                    {
                        for (int i = Enemies.Count - 1; i >= 0; i--)
                        {
                            if (Enemies[i] != null) Enemies[i].Vanish();
                        }
                        outsideTimer = 0f;
                        Debug.LogWarning("[GeoClash] 检测到场外卡死的敌人，已强制清除");
                    }
                }
                else
                {
                    outsideTimer = 0f;
                }

                if (pending.Count == 0 && Enemies.Count == 0)
                {
                    waveActive = false;
                    waveBreak = 2.2f;
                    outsideTimer = 0f;
                    if (Wave % 5 == 0) Bombs = Mathf.Min(5, Bombs + 1);
                    Hud.Announce("WAVE CLEAR", 1.5f);
                }
            }
            else
            {
                waveBreak -= dt;
                if (waveBreak <= 0f) StartWave(Wave + 1);
            }
        }

        // ------------------------------------------------------------ 生命周期

        public void StartGame()
        {
            Time.timeScale = 1f;
            Score = 0;
            Lives = 3;
            Bombs = 3;
            Wave = 0;
            Multiplier = 1f;
            multGrace = 0f;
            camInit = false;
            respawnTimer = 0f;
            gameOverDelay = 0f;

            ClearWorld();
            State = GameState.Playing;
            Player.Respawn(Vector3.zero);
            Sfx.Ui();
            StartWave(1);
        }

        void ClearWorld()
        {
            for (int i = Enemies.Count - 1; i >= 0; i--)
            {
                if (Enemies[i] != null) Destroy(Enemies[i].gameObject);
            }
            Enemies.Clear();
            pending.Clear();

            for (int i = 0; i < PlayerBullets.Count; i++) if (PlayerBullets[i] != null) PlayerBullets[i].Despawn();
            for (int i = 0; i < EnemyBullets.Count; i++) if (EnemyBullets[i] != null) EnemyBullets[i].Despawn();
            for (int i = 0; i < Geoms.Count; i++) if (Geoms[i] != null) Geoms[i].Despawn();
            PlayerBullets.Clear();
            EnemyBullets.Clear();
            Geoms.Clear();

            if (Fx.I != null) Fx.I.ClearAll();
            if (SplitFx.I != null) SplitFx.I.ClearAll();
            if (SpringGrid.I != null) SpringGrid.I.ResetGrid();
        }

        void Pause()
        {
            State = GameState.Paused;
            Time.timeScale = 0f;
            Sfx.Ui();
        }

        void Resume()
        {
            State = GameState.Playing;
            Time.timeScale = 1f;
            Sfx.Ui();
        }

        void GameOver()
        {
            State = GameState.GameOver;
            gameOverDelay = 0.8f;
            if (Score > Best)
            {
                Best = Score;
                PlayerPrefs.SetInt("geoclash_best", Best);
                PlayerPrefs.Save();
            }
            Sfx.BigExplode();
        }

        void KillPlayer()
        {
            if (!PlayerAlive) return;
            if (Player.Invuln > 0f) return;

            // 记住阵亡位置：复活是"就地重生"，黑洞也在原地展开
            deathPos = Player.transform.position;

            Player.Die();
            if (!InfiniteLives) Lives--;
            Multiplier = Mathf.Max(1f, Multiplier * 0.5f);

            if (!InfiniteLives && Lives < 0) GameOver();
            else respawnTimer = 1.5f;
        }

        void UpdateRespawn(float dt)
        {
            if (PlayerAlive || State != GameState.Playing) return;
            if (!InfiniteLives && Lives < 0) return;
            respawnTimer -= dt;
            if (respawnTimer <= 0f)
            {
                // 就地复活；显隐/锁定/无敌都由 Player 自己管
                Player.Respawn(deathPos);
            }
        }

        // ------------------------------------------------------------ 主循环

        void Update()
        {
            if (Cam == null) return;
            if (Mathf.Abs(Cam.aspect - lastAspect) > 0.002f) UpdateFraming();

            float dt = Mathf.Min(Time.deltaTime, 0.05f);
            UpdateCamera(dt);

            if (Controls.MutePressed()) Sfx.SetMuted(!Sfx.Muted);

            switch (State)
            {
                case GameState.Title:
                    Ambient(dt);
                    if (Controls.StartPressed()) StartGame();
                    return;

                case GameState.GameOver:
                    Ambient(dt);
                    gameOverDelay -= Time.unscaledDeltaTime;
                    if (gameOverDelay <= 0f && Controls.StartPressed()) StartGame();
                    return;

                case GameState.Paused:
                    if (Controls.PausePressed()) Resume();
                    return;
            }

            if (Controls.PausePressed()) { Pause(); return; }

            // 倍率：捡到 Geom 后有 2.5 秒缓冲，之后才缓慢衰减
            if (multGrace > 0f) multGrace -= dt;
            else Multiplier = Mathf.Max(1f, Multiplier - dt * 0.06f);

            UpdateSpawns(dt);
            UpdateWave(dt);
            UpdateRespawn(dt);
            UpdateCollisions();
            CleanupLists();
            UpdateBossHp();
        }

        void Ambient(float dt)
        {
            ambientTimer -= dt;
            if (ambientTimer > 0f) return;
            ambientTimer = 0.06f;
            Vector3 p = new Vector3(Random.Range(-AX, AX), Random.Range(-AY, AY), 0f);
            Fx.I.AddAmbient(p, new Color(0.2f, 0.7f, 1f, 0.5f));
        }

        void UpdateBossHp()
        {
            bossHp = -1f;
            for (int i = 0; i < Enemies.Count; i++)
            {
                var e = Enemies[i];
                if (e != null && e.Kind == EnemyKind.Boss)
                {
                    bossHp = e.Hp;
                    bossHpMax = e.MaxHp;
                    break;
                }
            }
        }

        void CleanupLists()
        {
            for (int i = PlayerBullets.Count - 1; i >= 0; i--)
            {
                if (PlayerBullets[i] == null || !PlayerBullets[i].Alive) PlayerBullets.RemoveAt(i);
            }
            for (int i = EnemyBullets.Count - 1; i >= 0; i--)
            {
                if (EnemyBullets[i] == null || !EnemyBullets[i].Alive) EnemyBullets.RemoveAt(i);
            }
            for (int i = Geoms.Count - 1; i >= 0; i--)
            {
                if (Geoms[i] == null || !Geoms[i].Alive) Geoms.RemoveAt(i);
            }
            for (int i = Enemies.Count - 1; i >= 0; i--)
            {
                if (Enemies[i] == null || Enemies[i].Dead) Enemies.RemoveAt(i);
            }
        }

        void UpdateCollisions()
        {
            // 玩家子弹 -> 敌人
            for (int i = 0; i < PlayerBullets.Count; i++)
            {
                var b = PlayerBullets[i];
                if (b == null || !b.Alive) continue;
                Vector3 bp = b.transform.position;

                for (int j = Enemies.Count - 1; j >= 0; j--)
                {
                    var e = Enemies[j];
                    if (e == null || e.Dead) continue;
                    // 用缓存的 Pos 而不是 e.transform.position：这是内层循环，每帧要跑
                    // 子弹数 × 敌人数 次，读 Transform 是原生调用（22.9 ns），
                    // 9 万对就是 4.4 ms；读字段只要 0.26 ms。
                    Vector3 ep = e.Pos;
                    float dx = bp.x - ep.x;
                    float dy = bp.y - ep.y;
                    float rr = b.Radius + e.Radius;
                    if (dx * dx + dy * dy < rr * rr)
                    {
                        e.Hit(b.Damage, b.Vel.normalized);
                        b.Despawn();
                        break;
                    }
                }
            }

            if (!PlayerAlive) return;

            Vector3 pp = PlayerPos;
            float pr = Player.Radius;

            // 敌人子弹 -> 玩家
            for (int i = 0; i < EnemyBullets.Count; i++)
            {
                var b = EnemyBullets[i];
                if (b == null || !b.Alive) continue;
                Vector3 bp = b.transform.position;
                float dx = bp.x - pp.x;
                float dy = bp.y - pp.y;
                float rr = b.Radius + pr;
                if (dx * dx + dy * dy < rr * rr)
                {
                    if (Player.Invuln > 0f)
                    {
                        b.Despawn();
                        Fx.I.Spark(bp, new Vector2(dx, dy), Palette.Bullet);
                        continue;
                    }
                    b.Despawn();
                    KillPlayer();
                    return;
                }
            }

            // 敌人本体 -> 玩家
            for (int i = 0; i < Enemies.Count; i++)
            {
                var e = Enemies[i];
                if (e == null || e.Dead) continue;
                Vector3 ep = e.Pos;
                float dx = ep.x - pp.x;
                float dy = ep.y - pp.y;
                float rr = e.Radius + pr * 0.8f;
                if (dx * dx + dy * dy < rr * rr)
                {
                    if (Player.Invuln > 0f) continue;
                    KillPlayer();
                    return;
                }
            }
        }

        public void UseBomb()
        {
            if (Bombs <= 0 || !PlayerAlive || State != GameState.Playing) return;
            Bombs--;

            Vector3 p = PlayerPos;
            Sfx.Bomb();
            Fx.I.Shake(1.2f);
            Fx.I.Shockwave(p, Palette.Player, 30f, 0.85f);
            Fx.I.Shockwave(p, Palette.White, 20f, 0.6f);
            Fx.I.Burst(p, Palette.Bullet, 70, 30f, 0.8f, 0.85f);

            // 整个弹簧网格被狠狠推一下，波纹会从中心扩散到边界再反射回来
            SpringGrid.Push(new Vector2(p.x, p.y), 45f, 340f);

            for (int i = Enemies.Count - 1; i >= 0; i--)
            {
                var e = Enemies[i];
                if (e == null || e.Dead) continue;
                Vector3 ep = e.Pos;
                Vector2 d = new Vector2(ep.x - p.x, ep.y - p.y);
                if (d.magnitude < 16f)
                {
                    e.Hit(14f, d.sqrMagnitude > 0.001f ? d.normalized : Vector2.up);
                }
            }

            for (int i = EnemyBullets.Count - 1; i >= 0; i--)
            {
                if (EnemyBullets[i] != null) EnemyBullets[i].Despawn();
            }

            AddScore(30);
        }
    }
}
