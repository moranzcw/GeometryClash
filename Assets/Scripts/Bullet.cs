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

        void Update()
        {
            if (!Alive) return;
            float dt = Time.deltaTime;
            Life -= dt;
            if (Life <= 0f) { Despawn(); return; }

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

        public void Despawn()
        {
            if (!Alive) return;
            Alive = false;
            if (Pool != null) Pool.Release(this);
            else gameObject.SetActive(false);
        }
    }
}
