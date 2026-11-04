using UnityEngine;

namespace GeoClash
{
    /// <summary>全局配色。数值允许大于 1（HDR），用来触发 Bloom 辉光。</summary>
    public static class Palette
    {
        public static readonly Color Bg = new Color(0.006f, 0.008f, 0.016f, 1f);

        // 网格：深蓝，比最初版本暗很多，但仍能看清结构（整体亮度 ×0.7）
        public static readonly Color GridMinor = new Color(0.0735f, 0.196f, 0.490f, 1f);
        public static readonly Color GridMajor = new Color(0.105f, 0.301f, 0.735f, 1f);
        // 场地边框单独保留一点亮度，用来标出边界
        public static readonly Color Border = new Color(0.090f, 0.420f, 0.820f, 1f);

        // 主角改成白色（冷白），和所有敌人都能拉开
        public static readonly Color Player = new Color(2.35f, 2.45f, 2.60f, 1f);
        public static readonly Color Bullet = new Color(0.55f, 2.30f, 3.00f, 1f);
        // 尾迹改成暖黄，像火星
        public static readonly Color Trail = new Color(2.60f, 1.35f, 0.25f, 1f);
        public static readonly Color TrailHot = new Color(2.60f, 2.20f, 0.85f, 1f);
        public static readonly Color Geom = new Color(0.34f, 1.50f, 0.36f, 1f);

        public static readonly Color Grunt = new Color(1.75f, 0.42f, 2.40f, 1f);
        public static readonly Color Wanderer = new Color(0.30f, 0.95f, 2.60f, 1f);
        // 主角已经是白色，Dodger 改成紫罗兰
        public static readonly Color Dodger = new Color(1.15f, 0.50f, 2.70f, 1f);
        public static readonly Color Gravity = new Color(2.55f, 1.05f, 0.20f, 1f);
        public static readonly Color Hole = new Color(2.10f, 0.20f, 1.65f, 1f);
        public static readonly Color Spinner = new Color(0.25f, 2.20f, 1.95f, 1f);
        public static readonly Color Chomper = new Color(2.55f, 0.32f, 0.30f, 1f);
        public static readonly Color Weevil = new Color(2.40f, 2.00f, 0.30f, 1f);
        public static readonly Color Boss = new Color(2.60f, 0.55f, 0.85f, 1f);
        public static readonly Color EnemyBullet = new Color(2.60f, 1.15f, 0.20f, 1f);

        public static readonly Color White = new Color(2.6f, 2.6f, 2.6f, 1f);
        public static readonly Color UiCyan = new Color(0.45f, 0.95f, 1f, 1f);
        public static readonly Color UiDim = new Color(0.55f, 0.70f, 0.80f, 1f);
    }

    /// <summary>Vector2(XY 游戏平面) 的小工具。平面固定在 z=0。</summary>
    public static class V
    {
        public static Vector2 Rotate90(this Vector2 v) { return new Vector2(-v.y, v.x); }
    }
}
