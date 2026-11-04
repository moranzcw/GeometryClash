using UnityEngine;

namespace GeoClash
{
    /// <summary>绿色小菱形 Geom：击杀敌人掉落，拾取提升倍率。</summary>
    public class Geom : MonoBehaviour
    {
        public Pool<Geom> Pool;
        public bool Alive;

        /// <summary>急吸半径 = 3 个大格子（大格 1.4 世界单位）。进这个圈就飞快扑向主角。</summary>
        const float MagnetRadius = 4.2f;
        /// <summary>进入这个半径就自动拾取。</summary>
        const float CollectRadius = 2.0f;
        /// <summary>缓慢吸引的作用范围，超出后基本只受飘动影响。</summary>
        const float AttractRange = 40f;
        /// <summary>出生后先自由飘这么久，再开始被吸引。</summary>
        const float DriftDelay = 0.35f;

        Vector2 vel;
        float life;
        float t;
        float spin;
        float phase;
        MeshRenderer rend;

        public static Geom Create(Transform parent)
        {
            var go = new GameObject("Geom");
            go.transform.SetParent(parent, false);
            var g = go.AddComponent<Geom>();
            g.rend = MatLib.Attach(go, Meshes.Poly(4, 0.42f, 0.13f, 0.25f, 45f), MatLib.Neon(Palette.Geom));
            return g;
        }

        public void Launch(Vector3 pos)
        {
            transform.position = pos;
            float a = Random.value * Mathf.PI * 2f;
            // 初速很柔：只是从爆点轻轻弹出来，剩下的交给飘动和吸引
            vel = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * Random.Range(1.6f, 3.6f);
            life = 13f;
            t = 0f;
            spin = Random.Range(-62f, 62f);
            phase = Random.value * Mathf.PI * 2f;
            Alive = true;
            transform.localScale = Vector3.one * Random.Range(0.8f, 1.2f);
            transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
            // 颜色已经由 Create 时的共享材质带上了，不需要再设 MPB
        }

        void Update()
        {
            if (!Alive) return;
            float dt = Time.deltaTime;
            t += dt;
            life -= dt;
            if (life <= 0f) { Despawn(); return; }

            Vector3 pp = Game.I.PlayerPos;
            Vector2 toP = new Vector2(pp.x - transform.position.x, pp.y - transform.position.y);
            float d = toP.magnitude;
            bool pull = Game.I.PlayerAlive && t > DriftDelay && d > 0.001f;

            if (pull)
            {
                Vector2 dir = toP / d;

                if (d < MagnetRadius)
                {
                    // 3 个大格以内：急吸，越靠近越猛（30 → 95）
                    float k = 1f - d / MagnetRadius;
                    float want = Mathf.Lerp(30f, 95f, k);
                    vel = Vector2.MoveTowards(vel, dir * want, 420f * dt);
                }
                else
                {
                    // 远处缓慢靠近，越近越快（2.4 → 11），加速度很低所以看起来是"飘过去"
                    float near = Mathf.InverseLerp(AttractRange, MagnetRadius, d);
                    float want = Mathf.Lerp(2.4f, 11f, near * near);
                    vel = Vector2.MoveTowards(vel, dir * want, 14f * dt);
                }

                // 飘动：垂直于当前速度的缓慢摆动，每个掉落物相位不同
                float wob = Mathf.Sin(t * 2.1f + phase) * 1.7f;
                Vector2 perp = new Vector2(-vel.y, vel.x);
                float pl = perp.magnitude;
                if (pl > 0.01f) vel += (perp / pl) * (wob * dt);
            }
            else
            {
                // 出生瞬间的自由飘移，慢慢减速
                vel *= 1f - Mathf.Clamp01(1.1f * dt);
            }

            Vector3 gp = transform.position + new Vector3(vel.x, vel.y, 0f) * dt;

            // 掉落物同样不许出框，撞到边界反弹
            float lx = Game.AX - 0.4f;
            float lz = Game.AY - 0.4f;
            if (gp.x > lx) { gp.x = lx; vel.x = -Mathf.Abs(vel.x) * 0.6f; }
            else if (gp.x < -lx) { gp.x = -lx; vel.x = Mathf.Abs(vel.x) * 0.6f; }
            if (gp.y > lz) { gp.y = lz; vel.y = -Mathf.Abs(vel.y) * 0.6f; }
            else if (gp.y < -lz) { gp.y = -lz; vel.y = Mathf.Abs(vel.y) * 0.6f; }

            transform.position = gp;
            transform.Rotate(0f, 0f, spin * dt, Space.World);

            if (Game.I.PlayerAlive && d < CollectRadius)
            {
                Collect();
                return;
            }

            // 呼吸闪烁：常态在 0.55~1.0 之间脉动；快消失时改成急促闪。
            // 用共享材质（_Color 乘一个系数）而不是 MaterialPropertyBlock：
            // 叠加混合下"rgb × 系数"和"alpha × 系数"结果一样，但共享材质能合批。
            float a = 0.55f + 0.45f * (Mathf.Sin(t * 7f) * 0.5f + 0.5f);
            if (life < 3f) a *= Mathf.Sin(t * 22f) * 0.5f + 0.5f;
            rend.sharedMaterial = MatLib.NeonScaled(Palette.Geom, a);
        }

        void Collect()
        {
            Game.I.CollectGeom(transform.position);
            Despawn();
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
