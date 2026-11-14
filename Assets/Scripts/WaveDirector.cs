using System.Collections.Generic;
using UnityEngine;

namespace GeoClash
{
    /// <summary>
    /// 波次编排器需要从外部拿到的全部能力。
    ///
    /// 之所以定一个接口而不是直接传 <see cref="Game"/>：这个类只关心
    /// "什么时候、在哪里、刷什么"，它不该认识计分、生命、相机这些东西。
    /// 接口只有 6 个成员，实现方是谁都行 —— 单元测试里塞个假实现就能跑。
    /// </summary>
    public interface IWaveHost
    {
        /// <summary>当前波次（由 host 持有，编排器只读）。</summary>
        int Wave { get; }

        /// <summary>主角位置：决定刷怪点该围着谁转。</summary>
        Vector3 PlayerPos { get; }

        /// <summary>要一个"远离主角、且在场地内"的出生点。</summary>
        Vector3 SpawnPoint();

        /// <summary>真正创建一只敌人。</summary>
        void SpawnAt(EnemyKind kind, Vector3 pos, float size);

        /// <summary>场上还活着的敌人数（含已死亡但未清理的，和原来的判定保持一致）。</summary>
        int AliveEnemyCount { get; }

        /// <summary>一波开始：显示波次、播音效。</summary>
        void OnWaveStarted(int wave);

        /// <summary>一波清空：结算奖励、显示 WAVE CLEAR。</summary>
        void OnWaveCleared(int wave);
    }

    /// <summary>
    /// 波次编排：抽签决定这一波出什么，排成一张"未来时刻表"，然后按时间逐只发放。
    ///
    /// 纯逻辑，不碰 Unity 的生命周期，也没有每帧的 Update 方法需要挂载 ——
    /// 由 <see cref="Game"/> 每帧调一次 <see cref="Update"/>。
    /// </summary>
    public class WaveDirector
    {
        /// <summary>每几波出一个 Boss。</summary>
        public const int BossEvery = 5;

        /// <summary>一波清空后到下一波之间的停顿。</summary>
        public const float BreakTime = 2.2f;

        /// <summary>一张待发放的出生单。位置在发放那一刻才算（Live），否则用预先算好的。</summary>
        struct SpawnReq
        {
            public EnemyKind Kind;
            public Vector3 Pos;
            public float Delay;
            public float Size;
            public bool Live;
        }

        // 下面四个数组必须一一对应，顺序不能错。
        static readonly EnemyKind[] Kinds =
        {
            EnemyKind.Grunt, EnemyKind.Wanderer, EnemyKind.Dodger, EnemyKind.Spinner,
            EnemyKind.Chomper, EnemyKind.Gravity, EnemyKind.Weevil, EnemyKind.Hole
        };
        static readonly int[] Weights = { 4, 3, 2, 2, 2, 1, 3, 1 };
        // 引力井第 3 波、黑洞第 5 波就登场（原先是 5 和 8，太靠后，很多人根本见不到）
        static readonly int[] UnlockWave = { 1, 1, 2, 3, 4, 3, 6, 5 };
        static readonly int[] Cost = { 2, 2, 3, 4, 3, 5, 2, 6 };

        readonly IWaveHost host;
        readonly List<SpawnReq> pending = new List<SpawnReq>();

        float breakTimer;

        public WaveDirector(IWaveHost host)
        {
            this.host = host;
        }

        /// <summary>这一波是否还在进行（还有没发完的、或场上还有敌人）。</summary>
        public bool Active { get; private set; }

        /// <summary>时刻表里还没发放的数量。</summary>
        public int PendingCount { get { return pending.Count; } }

        /// <summary>清空时刻表（重开一局时用）。</summary>
        public void Clear()
        {
            pending.Clear();
            Active = false;
            breakTimer = 0f;
        }

        /// <summary>每帧调一次：发放到点的敌人，并推进波次状态。</summary>
        public void Update(float dt)
        {
            ReleaseDueSpawns(dt);

            if (Active)
            {
                if (pending.Count == 0 && host.AliveEnemyCount == 0)
                {
                    Active = false;
                    breakTimer = BreakTime;
                    host.OnWaveCleared(host.Wave);
                }
            }
            else
            {
                breakTimer -= dt;
                if (breakTimer <= 0f) StartWave(host.Wave + 1);
            }
        }

        /// <summary>立刻开始第 n 波（不再等间隔）。</summary>
        public void StartWave(int n)
        {
            host.OnWaveStarted(n);
            Active = true;
            pending.Clear();

            bool boss = (n % BossEvery == 0);
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

            Schedule(n, counts, boss);
        }

        /// <summary>
        /// 把这一波要出的敌人摊平成列表、打散顺序、切成若干组排进时刻表。
        ///
        /// 组内几乎同时出现（一次"爆发"），组与组之间留一段基础间隔。
        /// 打散是为了避免"8 只同类一起冒出来"这种块状感。
        /// </summary>
        void Schedule(int n, Dictionary<EnemyKind, int> counts, bool boss)
        {
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
        }

        void ReleaseDueSpawns(float dt)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                var s = pending[i];
                s.Delay -= dt;
                if (s.Delay <= 0f)
                {
                    // Live 的位置在这里才算：围绕主角"此刻"的位置找远处的点。
                    // 一波要铺开几十秒，预先算好的坐标会落在主角几十秒前站的地方。
                    host.SpawnAt(s.Kind, s.Live ? host.SpawnPoint() : s.Pos, s.Size);
                    Sfx.Spawn();
                    pending.RemoveAt(i);
                }
                else
                {
                    pending[i] = s;
                }
            }
        }

        /// <summary>按权重抽一个种类。只考虑"已解锁 且 预算够"的那些。</summary>
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
    }
}
