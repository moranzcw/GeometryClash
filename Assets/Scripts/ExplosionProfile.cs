using System;
using UnityEngine;

namespace GeoClash
{
    /// <summary>
    /// 敌人爆炸的全部可调参数。
    /// 游戏内用的是 `ExplosionProfile.Default`，改这里会直接影响正式游戏。
    ///
    /// 只有四组：白光闪烁 / 主弹片 / 震屏 / 网格。
    /// 主爆云（火枝）、杂散粒子、细碎火花、慢速余烬、冲击波光环均已删除。
    /// </summary>
    [Serializable]
    public class ExplosionProfile
    {
        /// <summary>游戏内使用的默认参数。</summary>
        public static readonly ExplosionProfile Default = new ExplosionProfile();

        [Header("白光闪烁（柔和辉光，不是方框）")]
        public bool flashOn = true;
        [Tooltip("内层白闪的尺寸（会再乘 scale）")]
        public float flashSize = 2.6f;
        [Tooltip("内层白闪的寿命（秒）")]
        public float flashLife = 0.12f;
        [Tooltip("外层光晕相对内层的尺寸倍率")]
        public float flashOuterScale = 2.1f;
        [Tooltip("外层光晕相对内层的寿命倍率")]
        public float flashOuterTime = 1.7f;
        [Range(0f, 1f)] [Tooltip("外层光晕的亮度（内层固定为纯白）")]
        public float flashOuterStrength = 0.40f;

        [Header("主弹片（飞得最远、会撞墙回弹）")]
        public bool shardsOn = true;
        public float shardCount = 100f;
        public Vector2 shardSpeed = new Vector2(10f, 45f);
        public Vector2 shardLife = new Vector2(1.5f, 3f);
        public Vector2 shardSize = new Vector2(0.2f, 0.25f);
        public Vector2 shardDrag = new Vector2(2f, 3f);
        public Vector2 shardStretch = new Vector2(0.07f, 0.15f);

        [Header("震屏")]
        public bool shakeOn = true;
        public float shakeMin = 0.16f;
        public float shakeMax = 0.9f;

        [Header("网格")]
        public bool gridOn = true;
        public float gridPushRadius = 8.0f;
        public float gridPushStrength = 150f;

        public static float Rand(Vector2 r)
        {
            return UnityEngine.Random.Range(r.x, r.y);
        }
    }
}
