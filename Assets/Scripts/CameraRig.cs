using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace GeoClash
{
    /// <summary>
    /// 相机装配与跟随。原来这一坨（约 130 行）塞在 <see cref="Game"/> 里，
    /// 但取景和游戏逻辑没有任何关系 —— 拆出来后改镜头不用碰游戏流程。
    ///
    /// 做法：相机站在平面正前方（-Z），姿态永远由「保持水平 + 看向聚焦点」自动决定。
    /// 平移量由主角在场地里的相对位置驱动，所以画面是梯形透视而不是整体旋转。
    /// </summary>
    public class CameraRig
    {
        public Camera Cam { get; private set; }

        /// <summary>相机每帧的实际位置。粒子系统用它做"面向相机"的四边形。</summary>
        public Vector3 Base { get; private set; }

        // 视野（1.4 倍，原来 19 × 12.5 看不清敌群）
        const float TargetHalfW = 26.6f;
        const float TargetHalfH = 17.5f;

        const float SwayX = 8.0f;        // 水平平移幅度
        const float SwayY = 6.5f;        // 垂直平移幅度
        const float DollyAmount = 0.03f; // 推拉呼吸幅度
        const float FollowSharpness = 9f;
        const float EdgeSlack = 12f;     // 聚焦点最多越出场地多少

        float lastAspect = -1f;
        float camDist = 33f;
        float viewHalfW = 31f;
        float viewHalfH = 17.5f;
        Vector3 focus;
        bool init;

        public void Build(Transform parent)
        {
            var go = new GameObject("MainCamera");
            go.tag = "MainCamera";
            go.transform.SetParent(parent, false);

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

        /// <summary>窗口比例变了（比如全屏切换）——需要重算取景距离。</summary>
        public bool AspectChanged
        {
            get { return Cam != null && Mathf.Abs(Cam.aspect - lastAspect) > 0.002f; }
        }

        /// <summary>
        /// 按"想看到多大一块场地"反推相机距离。
        /// 平面立在 z=0，相机在 -Z 正对着它，可见范围就是简单的透视投影：
        /// halfH = dist·tan(fov/2)，halfW = halfH·aspect。
        /// </summary>
        public void UpdateFraming()
        {
            if (Cam == null) return;
            lastAspect = Cam.aspect;

            float halfTan = Mathf.Tan(Cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
            float distH = TargetHalfH / halfTan;
            float distW = TargetHalfW / (halfTan * Mathf.Max(0.35f, Cam.aspect));
            camDist = Mathf.Max(distH, distW);

            viewHalfH = halfTan * camDist;
            viewHalfW = viewHalfH * Cam.aspect;

            Apply();
        }

        /// <summary>
        /// 跟随主角。聚焦点夹在场地内留一点边距，场地外的部分露出黑底。
        /// 视野变大后几乎和场地一样宽，留给"跟随"的余量很小 ——
        /// 如果强行让主角永远居中，镜头必须整块移出场地，反而会丢失边框。
        /// </summary>
        public void Follow(Vector3 playerPos, Vector2 playerVel, bool playerAlive, float dt)
        {
            Vector3 target = playerPos;

            // 前瞻：镜头略微偏向移动方向，强化速度感
            if (playerAlive) target += new Vector3(playerVel.x, playerVel.y, 0f) * 0.12f;

            float minX = -Arena.HalfW - EdgeSlack + viewHalfW;
            float maxX = Arena.HalfW + EdgeSlack - viewHalfW;
            float minY = -Arena.HalfH - EdgeSlack + viewHalfH;
            float maxY = Arena.HalfH + EdgeSlack - viewHalfH;

            if (minX > maxX) { float m = (minX + maxX) * 0.5f; minX = m; maxX = m; }
            if (minY > maxY) { float m = (minY + maxY) * 0.5f; minY = m; maxY = m; }

            target.x = Mathf.Clamp(target.x, minX, maxX);
            target.y = Mathf.Clamp(target.y, minY, maxY);
            target.z = 0f;

            if (!init)
            {
                focus = target;
                init = true;
            }
            else
            {
                focus = Vector3.Lerp(focus, target, 1f - Mathf.Exp(-FollowSharpness * dt));
            }

            Apply();
        }

        /// <summary>下一帧直接把聚焦点对准目标（重开一局时用，避免镜头从上一局的位置滑过来）。</summary>
        public void ResetFollow()
        {
            init = false;
        }

        void Apply()
        {
            float t = Time.time;

            // 推拉呼吸：只改距离，不改方向
            float dist = camDist * (1f + Mathf.Sin(t * 0.13f) * DollyAmount);

            // 主角在场地里的归一化位置（-1 = 最左/最下，+1 = 最右/最上）
            float nx = Mathf.Clamp(focus.x / Arena.HalfW, -1f, 1f);
            float ny = Mathf.Clamp(focus.y / Arena.HalfH, -1f, 1f);

            Base = new Vector3(focus.x + nx * SwayX, focus.y + ny * SwayY, -dist);

            if (Cam != null)
            {
                Cam.transform.position = Base;
                Cam.transform.rotation = Quaternion.LookRotation(focus - Base, Vector3.up);
            }
        }
    }
}
