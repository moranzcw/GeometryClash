using UnityEngine;

namespace GeoClash
{
    /// <summary>玩家飞船：双摇杆移动 + 瞄准，冲刺，炸弹。</summary>
    public class Player : MonoBehaviour
    {
        public float Radius = 0.5f;
        public bool Alive = true;
        public float Invuln;

        public Vector2 AimDir = Vector2.up;

        float visualYaw;

        const float Speed = 16.1f;
        const float Accel = 147f;
        const float DashSpeed = 38f;

        Vector2 vel;
        float fireCd;
        float dashCd;
        float dashTime;
        Vector2 dashDir;
        float trailTimer;

        /// <summary>最近一次的移动方向。静止时尾焰沿这个方向的反向继续喷，
        /// 不会跟着鼠标转（否则站着不动转准心，尾焰会跟着甩）。</summary>
        Vector2 trailDir = Vector2.up;

        Transform visual;
        MeshRenderer rend;

        /// <summary>无敌环：独立于 visual，隐身/显隐互不影响。</summary>
        Transform ringTr;
        MeshRenderer ringRend;
        /// <summary>环半径 = 主角尺寸的 1.5 倍左右（飞船包围盒约 2.1×1.75）</summary>
        const float RingScale = 1.6f;

        /// <summary>出场动画剩余时间：黑洞吸完才现身，期间不接受输入。</summary>
        float spawnTimer;

        /// <summary>复活后的无敌时长。</summary>
        public const float InvulnTime = 3f;

        public Vector2 Velocity { get { return vel; } }

        // ------------------------------------------------------------ 机身边缘

        /// <summary>
        /// 飞船轮廓，顺序与 Meshes.Ship() 的 hull 完全一致（局部坐标，机头朝 +Y）。
        /// 尾焰要从机身边缘冒出来，所以需要这份轮廓来做射线求交。
        /// </summary>
        static readonly Vector2[] Hull =
        {
            new Vector2(-0.42f,  0.95f),   // 前缘左
            new Vector2( 0.00f,  0.78f),   // 前缘中点内凹
            new Vector2( 0.42f,  0.95f),   // 前缘右
            new Vector2( 1.05f,  0.10f),   // 右肩
            new Vector2( 0.62f, -0.80f),   // 右后角
            new Vector2( 0.00f, -0.55f),   // 尾部凹口
            new Vector2(-0.62f, -0.80f),   // 左后角
            new Vector2(-1.05f,  0.10f),   // 左肩
        };

        /// <summary>
        /// 从机身中心沿 worldDir 射出，返回射线与机身轮廓的交点（世界坐标）。
        ///
        /// 用来把尾焰的起点推到"被喷向的那一侧边缘"：往右飞就从左边缘喷，往上飞就从尾口喷，
        /// 而不是永远从机身中心冒出来。机身是绕 Z 旋转 visualYaw 的，所以要把方向转到
        /// 局部空间求交，再转回世界。
        /// </summary>
        Vector3 EdgePoint(Vector2 worldDir)
        {
            if (worldDir.sqrMagnitude < 1e-6f) return transform.position;

            float yaw = visualYaw * Mathf.Deg2Rad;
            float cs = Mathf.Cos(-yaw), sn = Mathf.Sin(-yaw);

            // 世界方向 -> 局部方向（绕 Z 逆旋转 yaw）
            Vector2 d = worldDir.normalized;
            Vector2 ld = new Vector2(d.x * cs - d.y * sn, d.x * sn + d.y * cs);

            // 射线 P = t·ld 与每条边的交点，取最近的 t>0
            float best = float.MaxValue;
            for (int i = 0; i < Hull.Length; i++)
            {
                Vector2 a = Hull[i];
                Vector2 e = Hull[(i + 1) % Hull.Length] - a;

                float den = ld.x * e.y - ld.y * e.x;          // ld × e
                if (Mathf.Abs(den) < 1e-6f) continue;

                float t = (a.x * e.y - a.y * e.x) / den;      // a × e
                float u = (a.x * ld.y - a.y * ld.x) / den;    // a × ld
                if (t > 0f && u >= 0f && u <= 1f && t < best) best = t;
            }

            if (best == float.MaxValue) return transform.position;

            Vector2 local = ld * best;

            // 局部 -> 世界（绕 Z 正旋转 yaw）
            float cs2 = Mathf.Cos(yaw), sn2 = Mathf.Sin(yaw);
            Vector2 w = new Vector2(local.x * cs2 - local.y * sn2, local.x * sn2 + local.y * cs2);
            return transform.position + new Vector3(w.x, w.y, 0f);
        }

        public void Init()
        {
            var go = new GameObject("v");
            visual = go.transform;
            visual.SetParent(transform, false);
            rend = MatLib.Attach(go, Meshes.Ship(), MatLib.Neon(Palette.Player));

            // 无敌提示环
            var ringGo = new GameObject("InvulnRing");
            ringTr = ringGo.transform;
            ringTr.SetParent(transform, false);
            ringRend = MatLib.Attach(ringGo, Meshes.Ring(64, 1f, 0.055f), MatLib.Neon(Palette.Bullet));
            ringTr.localScale = Vector3.one * RingScale;
            ringGo.SetActive(false);
        }

        public void SetVisible(bool v)
        {
            if (visual != null) visual.gameObject.SetActive(v);
            if (!v) HideRing();
        }

        void HideRing()
        {
            if (ringTr != null && ringTr.gameObject.activeSelf) ringTr.gameObject.SetActive(false);
        }

        public void Respawn(Vector3 pos, bool fx = true)
        {
            transform.position = pos;
            vel = Vector2.zero;
            Alive = true;
            dashTime = 0f;
            dashCd = 0f;
            fireCd = 0f;
            AimDir = Vector2.up;
            trailDir = AimDir;      // 刚复活还没有"上一次移动方向"，先用当前朝向
            visualYaw = 0f;
            trailTimer = 0f;
            // 复位到基础材质（上一次无敌闪烁可能把材质换成了暗档）
            rend.sharedMaterial = MatLib.Neon(Palette.Player);

            // 主角和黑洞同时出现：立刻显示，但 spawnTimer 期间不能动
            spawnTimer = fx ? Mathf.Max(0f, SuctionProfile.Default.duration) : 0f;
            SetVisible(true);

            // 无敌时长从"能动了"那一刻算满 3 秒，所以把锁定时间加进去
            Invuln = InvulnTime + spawnTimer;

            if (fx)
            {
                // 网格往中心塌陷 + 激光扫过音效 + 诞生弹片，三件事同时发生
                SpringGrid.Suction(new Vector2(pos.x, pos.y), SuctionProfile.Default);
                Fx.I.PlayerSpawn(pos, Palette.Player);
                Sfx.PlayerSpawn();
            }
        }

        public void Die()
        {
            if (!Alive) return;
            Alive = false;
            vel = Vector2.zero;
            dashTime = 0f;
            spawnTimer = 0f;
            SetVisible(false);
            HideRing();
            // 主角死亡走和敌人同一套爆炸（白闪 + 主弹片），只是规模更大
            Fx.I.Explosion(transform.position, Palette.Player, 1.6f);
            Fx.I.Shockwave(transform.position, Palette.Player, 12f, 0.7f);
            Fx.I.Shake(1.1f);
            Sfx.Death();
        }

        /// <summary>无敌闪烁 + 无敌环。出场动画期间也走这里，保证 3 秒是连着的。</summary>
        void UpdateInvuln(float dt)
        {
            if (Invuln <= 0f)
            {
                HideRing();
                return;
            }

            Invuln -= dt;

            // 机身闪烁。注意飞船颜色是 HDR（约 2.4），alpha 乘 0.22 后叠加混合 + Bloom
            // 看起来几乎没变暗（实测这就是"看不见闪烁"的原因），所以暗相要压到 0.05~0.10。
            // 出场那 0.2 秒再提高频率，保证肉眼能数到两三次闪。
            bool spawning = spawnTimer > 0f;
            float rate = spawning ? 78f : 30f;
            float dim = spawning ? 0.05f : 0.10f;
            bool blinkOn = Mathf.Sin(Time.time * rate) > 0f;
            float a = blinkOn ? dim : 1f;
            // 用共享材质（_Color 乘系数）代替 MaterialPropertyBlock —— 叠加混合下两者等价，
            // 但共享材质不会被踢出 SRP Batcher
            rend.sharedMaterial = MatLib.NeonScaled(Palette.Player, a);

            // 无敌环：跟着机身一起闪，闪烁感才明显
            bool visible = visual != null && visual.gameObject.activeSelf;
            if (ringTr != null && visible)
            {
                if (!ringTr.gameObject.activeSelf) ringTr.gameObject.SetActive(true);
                float pulse = 0.5f + 0.5f * Mathf.Sin(Time.time * 9f);
                float fade = Mathf.Clamp01(Invuln / 0.4f);     // 最后 0.4 秒淡出
                float ringA = (0.30f + 0.35f * pulse) * fade * (blinkOn ? 0.25f : 1f);
                var c = Palette.Bullet;
                ringRend.sharedMaterial = MatLib.NeonScaled(c, ringA);
                ringTr.localScale = Vector3.one * (RingScale + 0.07f * pulse);
            }
            else HideRing();

            if (Invuln <= 0f)
            {
                rend.sharedMaterial = MatLib.Neon(Palette.Player);
                HideRing();
            }
        }

        void Update()
        {
            if (Game.I == null || !Alive) return;
            if (Game.I.State != GameState.Playing) return;

            float dt = Time.deltaTime;
            if (dt <= 0f) return;

            // 出场锁定：机身已经出现、在闪烁，但还不能动
            if (spawnTimer > 0f)
            {
                spawnTimer -= dt;
                UpdateInvuln(dt);
                if (spawnTimer <= 0f) spawnTimer = 0f;
                return;
            }

            Vector2 move = Controls.Move();

            // 冲刺
            dashCd = Mathf.Max(0f, dashCd - dt);
            if (dashTime > 0f)
            {
                dashTime -= dt;
                vel = dashDir * DashSpeed;
                // 冲刺时喷得更猛；起点同样落在机身背面的边缘
                Fx.I.PlayerTrail(EdgePoint(-dashDir), dashDir, 1f);
            }
            else
            {
                Vector2 target = move * Speed;
                vel = Vector2.MoveTowards(vel, target, Accel * dt);

                if (Controls.DashPressed() && dashCd <= 0f)
                {
                    dashDir = move.sqrMagnitude > 0.01f ? move.normalized : AimDir;
                    dashTime = 0.13f;
                    dashCd = 0.85f;
                    Invuln = Mathf.Max(Invuln, 0.32f);
                    Sfx.Ui();
                }
            }

            // 位移（引力井/黑洞已经不再拉扯主角，这里只剩自身速度）
            Vector3 p = transform.position + new Vector3(vel.x, vel.y, 0f) * dt;
            p.x = Mathf.Clamp(p.x, -Game.AX + Radius, Game.AX - Radius);
            p.y = Mathf.Clamp(p.y, -Game.AY + Radius, Game.AY - Radius);
            transform.position = p;

            // 瞄准（同时决定子弹方向和机身朝向）
            AimDir = Controls.Aim(transform.position);

            if (visual != null && visual.gameObject.activeSelf)
            {
                float targetYaw = Mathf.Atan2(-AimDir.x, AimDir.y) * Mathf.Rad2Deg;
                visualYaw = Mathf.MoveTowardsAngle(visualYaw, targetYaw, 1440f * dt);
                visual.localRotation = Quaternion.Euler(0f, 0f, visualYaw);
            }

            // 开火
            fireCd -= dt;
            if (Controls.FireHeld() && fireCd <= 0f)
            {
                fireCd = 0.085f;
                Fire();
            }

            // 粒子拖尾：起点在机身"背面"的边缘（往哪边喷就从哪边的边缘冒出来），
            // 静止时也喷，方向保持"上一次移动的方向"，
            // 不会跟着准心转 —— 只有真正移动时才更新这个方向。
            float speed01 = Mathf.Clamp01(vel.magnitude / Speed);
            if (vel.sqrMagnitude > 0.5f) trailDir = vel.normalized;

            trailTimer -= dt;
            if (trailTimer <= 0f)
            {
                trailTimer = 0.014f;
                Fx.I.PlayerTrail(EdgePoint(-trailDir), trailDir, dashTime > 0f ? 1f : speed01);
            }

            // 无敌闪烁 + 无敌环
            UpdateInvuln(dt);

            if (Controls.BombPressed()) Game.I.UseBomb();
        }

        void Fire()
        {
            // 三发扇形：总张角 7°，即 -3.5° / 0° / +3.5°
            const float spreadDeg = 7f;
            const int count = 3;

            for (int i = 0; i < count; i++)
            {
                float t = (i / (float)(count - 1)) - 0.5f;      // -0.5, 0, +0.5
                float ang = t * spreadDeg * Mathf.Deg2Rad;
                float cs = Mathf.Cos(ang), sn = Mathf.Sin(ang);
                Vector2 dir = new Vector2(AimDir.x * cs - AimDir.y * sn, AimDir.x * sn + AimDir.y * cs);

                Vector3 origin = transform.position + new Vector3(dir.x, dir.y, 0f) * 0.95f;

                // 子弹速度叠加自身速度在射击方向上的分量：
                // 顺着瞄准方向往前冲 → 子弹更快；倒退着打 → 子弹更慢。
                // 只取"沿射击方向"的分量（点乘），横向平移不会把子弹推歪，
                // 子弹始终沿着准心飞。
                float inherit = Vector2.Dot(vel, dir);
                Game.I.PlayerShoot(origin, dir, inherit);

                if (i == count / 2) Fx.I.Spark(origin, dir, Palette.Bullet);
            }

            Sfx.Shoot();
        }
    }
}
