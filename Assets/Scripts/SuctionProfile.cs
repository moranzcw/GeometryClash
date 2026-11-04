using System;
using UnityEngine;

namespace GeoClash
{
    /// <summary>
    /// 黑洞吸附（主角复活时的网格坍缩）的全部可调参数。
    ///
    /// 物理模型：对半径内的每个质点施加指向中心的加速度
    ///     a = strength × edge / (dist^power + soften)
    /// 距离越近 a 越大 —— 越靠近中心吸得越狠，所以质点会一路加速往中心掉，
    /// 整个网格持续坍缩，而不是"塌到某个位置就静止"。
    ///
    /// 游戏内用的是 SuctionProfile.Default，改这里会直接影响主角复活的坍缩效果。
    /// </summary>
    [Serializable]
    public class SuctionProfile
    {
        /// <summary>游戏内使用的默认参数。</summary>
        public static readonly SuctionProfile Default = new SuctionProfile();

        [Header("作用范围")]
        [Tooltip("作用半径：超出这个距离的质点完全不受影响")]
        public float radius = 40f;

        [Header("引力")]
        [Tooltip("基准强度：距离 1 处的加速度（单位/秒²）。越大整片塌得越快")]
        public float strength = 2000000000f;

        [Tooltip("衰减幂次：1 = 1/r（线性衰减），2 = 1/r²（越近越猛，更像黑洞），0.5 = 更平")]
        public float power = 1f;

        [Tooltip("软化半径：避免中心处力发散。越大中心越温和、越不容易把网格拉成一团")]
        public float soften = 6.51f;

        [Header("吞没")]
        [Tooltip("吞没半径：质点掉进这个范围就被钉在中心，防止穿过圆心跑到对面去")]
        public float capture = 0f;

        [Header("时间")]
        [Tooltip("持续时长（秒）。主角也是在这个时间点解除锁定")]
        public float duration = 0.2f;

        [Tooltip("淡入时长（秒），避免一上来就硬拉")]
        public float fadeIn = 0f;

        [Tooltip("淡出时长（秒），避免突然断力")]
        public float fadeOut = 0f;
    }
}
