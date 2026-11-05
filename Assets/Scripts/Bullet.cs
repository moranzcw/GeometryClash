using UnityEngine;

namespace GeoClash
{
    /// <summary>子弹（玩家 / 敌人共用，通过 Friendly 区分）。</summary>
    public class Bullet : MonoBehaviour
    {
        public Vector2 Vel;
        public float Life;
        public float Radius = 0.18f;
        public float Damage = 1f;
        public bool Friendly;
        public bool Alive;
        public Pool<Bullet> Pool;

        MeshRenderer rend;
        MeshFilter filter;
        Color color = Color.white;

        public static Bullet Create(Transform parent)
        {
            var go = new GameObject("Bullet");
            go.transform.SetParent(parent, false);
            var b = go.AddComponent<Bullet>();
            b.filter = go.AddComponent<MeshFilter>();
            b.rend = go.AddComponent<MeshRenderer>();
            b.rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            b.rend.receiveShadows = false;
            return b;
        }

        public void Launch(Vector3 pos, Vector2 vel, bool friendly, Color c)
        {
            transform.position = pos;
            Vel = vel;
            Friendly = friendly;
            Alive = true;
            color = c;
            Life = friendly ? 1.5f : 6f;
            Radius = friendly ? 0.22f : 0.30f;
            Damage = 1f;

            if (friendly)
            {
                filter.sharedMesh = Meshes.Rect(0.17f, 0.95f);
                transform.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(-vel.x, vel.y) * Mathf.Rad2Deg);
            }
            else
            {
                filter.sharedMesh = Meshes.Ring(14, 0.34f, 0.09f);
                transform.rotation = Quaternion.identity;
            }

            // 材质已经带着颜色了，不再用 MaterialPropertyBlock（那会把子弹踢出 SRP Batcher，
            // 同屏上百发就是上百次 SetPass）
            rend.sharedMaterial = MatLib.Neon(c);
        }

        /// <summary>力场要不要也弯折敌方子弹。默认只影响玩家子弹（那是防御机制）。</summary>
        const bool FieldsAffectEnemyBullets = false;

        void Update()
        {
            if (!Alive) return;
            float dt = Time.deltaTime;
            Life -= dt;
            if (Life <= 0f) { Despawn(); return; }

            // 先被力场弯折弹道，再按新方向前进
            ApplyFields(dt);

            Vector3 p = transform.position + new Vector3(Vel.x, Vel.y, 0f) * dt;
            transform.position = p;

            // 子弹飞过时把弹簧网格沿着飞行方向顶出一道波（而不是压一个坑）。
            // 波峰在子弹前方，所以看起来像船头推开的浪，一路跟着子弹走。
            // 主角子弹的推力是敌方子弹的两倍多（9 vs 2.4），让玩家开火时网格反应更明显。
            Vector2 vdir = Vel.sqrMagnitude > 1e-4f ? Vel.normalized : Vector2.up;
            SpringGrid.PushDirected(new Vector2(p.x, p.y), vdir, 2.4f, Friendly ? 9f : 2.4f);

            // 撞到竞技场边界：就地炸开，绝不飞出方框
            Vector2 normal = Vector2.zero;
            if (p.x >= Game.AX) normal = new Vector2(-1f, 0f);
            else if (p.x <= -Game.AX) normal = new Vector2(1f, 0f);
            else if (p.y >= Game.AY) normal = new Vector2(0f, -1f);
            else if (p.y <= -Game.AY) normal = new Vector2(0f, 1f);

            if (normal != Vector2.zero)
            {
                p.x = Mathf.Clamp(p.x, -Game.AX, Game.AX);
                p.y = Mathf.Clamp(p.y, -Game.AY, Game.AY);
                transform.position = p;

                Fx.I.Impact(p, normal, color, Friendly ? 1f : 0.7f);
                Despawn();
            }
        }

        /// <summary>
        /// 引力井 / 黑洞的力场：把经过的子弹弯折。
        ///
        /// 关键做法：力只取**垂直于弹道**的分量。
        ///  - 正对着场心飞来的子弹，这个分量恰好是 0 → 完全偏转不了（想要的手感）。
        ///  - 只改变方向、不改变速度大小，所以子弹不会在半路被顶停或者反弹。
        ///  - 离场心越近弯得越快，边缘线性收到 0，不会有一圈硬边界。
        /// </summary>
        void ApplyFields(float dt)
        {
            if (!Friendly && !FieldsAffectEnemyBullets) return;

            var list = Enemy.Fields;
            if (list == null || list.Count == 0) return;

            float sp = Vel.magnitude;
            if (sp < 1e-3f) return;

            Vector2 bp = new Vector2(transform.position.x, transform.position.y);
            Vector2 vdir = Vel / sp;

            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null || e.Dead) continue;

                float radius, bend, sign;
                if (e.Kind == EnemyKind.Gravity)
                {
                    radius = Enemy.GravityFieldRadius;
                    bend = Enemy.GravityBendRate;
                    sign = -1f;              // 负号 = 把弹道往场心掰
                }
                else if (e.Kind == EnemyKind.Hole)
                {
                    radius = Enemy.HoleFieldRadius;
                    bend = Enemy.HoleBendRate;
                    sign = 1f;               // 正号 = 把弹道往外推
                }
                else continue;

                Vector2 d = bp - new Vector2(e.Pos.x, e.Pos.y);
                float dist = d.magnitude;
                if (dist > radius || dist < 1e-3f) continue;

                float k = 1f - dist / radius;
                Vector2 rad = d / dist;

                // perp 的大小正好是 sin(径向, 弹道) —— 正对着飞时它趋近 0
                Vector2 perp = rad - Vector2.Dot(rad, vdir) * vdir;
                vdir = (vdir + perp * (sign * bend * k * dt)).normalized;
            }

            Vel = vdir * sp;   // 速度大小严格不变，只改方向
        }

        public void Despawn()
        {
            if (!Alive) return;
            Alive = false;
            if (Pool != null) Pool.Release(this);
            else gameObject.SetActive(false);
        }
    }
}
