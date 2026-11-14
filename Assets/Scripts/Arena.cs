namespace GeoClash
{
    /// <summary>
    /// 场地的几何常量。
    ///
    /// 这几个数以前挂在 <see cref="Game"/> 上（`Game.AX` / `Game.AY`），
    /// 结果是 SpringGrid、Enemy、Bullet 这些根本不需要认识"游戏管理器"的模块，
    /// 都为了读一个尺寸而被绑到了 Game 上。
    ///
    /// 现在它们属于场地本身，谁需要谁引用，不经过 Game。
    /// </summary>
    public static class Arena
    {
        /// <summary>场地半宽（世界单位）。82.6 宽 = 59 个大格 × 1.4。</summary>
        public const float HalfW = 41.3f;

        /// <summary>场地半高（世界单位）。49.0 高 = 35 个大格 × 1.4。</summary>
        public const float HalfH = 24.5f;

        /// <summary>一个大格的边长（4 个小格 × 0.35）。</summary>
        public const float MajorCell = 1.4f;
    }
}
