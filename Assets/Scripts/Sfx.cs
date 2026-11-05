using UnityEngine;

namespace GeoClash
{
    /// <summary>程序化合成音效——不依赖任何音频资源，全部在运行时生成波形。</summary>
    public static class Sfx
    {
        const int SR = 44100;

        static AudioSource[] sources;
        static int cursor;
        static AudioSource music;
        static AudioClip sShoot, sHit, sExplode, sBigExplode, sGeom, sDeath, sBomb, sWave, sSpawn, sUi, sSpawnShip;
        static float lastSpawnSfxAt = -1f;

        public static bool Muted;
        public static float Master = 0.55f;
        public static float MusicVolume = 0.30f;

        /// <summary>静音开关（同时作用于音效和背景音乐）。</summary>
        public static void SetMuted(bool v)
        {
            Muted = v;
            if (music != null) music.mute = v;
        }

        public static void Init(Transform parent)
        {
            // 关闭域重载时静态字段会残留，这里判断一下旧对象是否已被销毁
            if (sources != null && sources.Length > 0 && sources[0] != null) return;

            sources = new AudioSource[0];

            // 无头 / 批处理模式下没有音频设备，直接跳过，避免卡死
            if (Application.isBatchMode) return;

            var go = new GameObject("Sfx");
            go.transform.SetParent(parent, false);
            sources = new AudioSource[10];
            for (int i = 0; i < sources.Length; i++)
            {
                var s = go.AddComponent<AudioSource>();
                s.playOnAwake = false;
                s.spatialBlend = 0f;
                s.volume = 1f;
                s.bypassReverbZones = true;
                sources[i] = s;
            }

            // 子弹：低沉的"扫过"声，纯气流、没有音调也没有爆破音
            sShoot = Swish("shoot", 0.090f, 300f, 1300f, 11f, 90f, 0.55f);
            // 命中：一阵短促的风（噪声过 4 级低通，慢起音），不用任何瞬态，所以不会有鞭炮感
            sHit = Wind("hit", 0.22f, 1400f, 520f, 0.014f, 1.6f, 0.42f);
            // 爆炸：低频下扫 + 两级低通噪声，去掉未滤波白噪（原来那种"鞭炮"感的来源）
            sExplode = Blast("explode", 0.85f, 70f, 55f, 2.4f, 0.30f, 2.0f, 320f, 40f, 0f, 22f, 0.55f);
            sBigExplode = Blast("bigExplode", 2.00f, 62f, 40f, 1.4f, 0.32f, 1.2f, 300f, 35f, 0f, 24f, 0.80f);
            // 拾取：比原来低一个八度左右，不再是尖锐的"叮"
            sGeom = Tone("geom", 0.11f, 420f, 700f, 0.05f, 10f, 0.36f);
            // 死亡：带一点下滑的闷响，不用方波音（那是"玩具"的来源）
            sDeath = Blast("death", 1.60f, 170f, 40f, 1.2f, 0.55f, 1.8f, 460f, 55f, 0f, 13f, 0.75f);
            // 炸弹：最低最长的下扫，完全不要高频瞬态 → 沉闷的"轰"
            sBomb = Blast("bomb", 2.60f, 55f, 32f, 1.1f, 0.30f, 0.9f, 260f, 30f, 0f, 26f, 0.90f);
            sWave = Tone("wave", 0.35f, 420f, 900f, 0f, 5f, 0.30f);
            sSpawn = Tone("spawn", 0.16f, 260f, 760f, 0.15f, 10f, 0.18f);
            // 主角出场：厚重的低频冲击（不是电子扫频音），带一点高频爆点
            sSpawnShip = Blast("spawnShip", 0.85f, 110f, 24f, 2.0f, 1.20f, 3.0f, 600f, 50f, 0.70f, 24f, 0.85f);
            sUi = Tone("ui", 0.10f, 720f, 1180f, 0f, 12f, 0.30f);

            StartMusic(parent);
        }

        /// <summary>启动程序化生成的背景音乐（16 秒无缝循环）。</summary>
        static void StartMusic(Transform parent)
        {
            if (music != null) return;

            var go = new GameObject("Music");
            go.transform.SetParent(parent, false);

            music = go.AddComponent<AudioSource>();
            music.playOnAwake = false;
            music.spatialBlend = 0f;
            music.loop = true;
            music.volume = MusicVolume;
            music.clip = MakeMusic();
            music.mute = Muted;
            music.Play();
        }

        static void Play(AudioClip clip, float vol, float pitch)
        {
            if (clip == null || sources == null || sources.Length == 0 || Muted) return;
            var s = sources[cursor];
            cursor = (cursor + 1) % sources.Length;
            s.pitch = pitch;
            s.PlayOneShot(clip, Mathf.Clamp01(vol * Master));
        }

        public static void Shoot() { Play(sShoot, 1f, Random.Range(0.94f, 1.07f)); }
        public static void Hit() { Play(sHit, 1f, Random.Range(0.85f, 1.2f)); }
        public static void Explode() { Play(sExplode, 1f, Random.Range(0.85f, 1.15f)); }
        public static void BigExplode() { Play(sBigExplode, 1f, Random.Range(0.9f, 1.05f)); }
        public static void Geom() { Play(sGeom, 1f, Random.Range(0.92f, 1.25f)); }
        public static void Death() { Play(sDeath, 1f, 1f); }
        public static void Bomb() { Play(sBomb, 1f, 1f); }
        public static void Wave() { Play(sWave, 1f, 1f); }
        /// <summary>
        /// 敌人出场音。均匀刷怪后每秒会冒 5~7 只，直接一只一声会变成机关枪，
        /// 所以这里节流到 0.09 秒一次（听感上仍是"连成一片"，但不会糊）。
        /// </summary>
        public static void Spawn()
        {
            if (Time.unscaledTime - lastSpawnSfxAt < 0.09f) return;
            lastSpawnSfxAt = Time.unscaledTime;
            Play(sSpawn, 1f, Random.Range(0.9f, 1.1f));
        }
        /// <summary>主角出场：激光扫过。</summary>
        public static void PlayerSpawn() { Play(sSpawnShip, 1f, Random.Range(0.97f, 1.03f)); }
        public static void Ui() { Play(sUi, 1f, 1f); }

        // ---------------- 波形合成 ----------------

        static AudioClip Tone(string name, float dur, float f0, float f1, float noiseMix, float decay, float gain)
        {
            int n = Mathf.Max(8, Mathf.CeilToInt(SR * dur));
            var data = new float[n];
            float phase = 0f;
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float k = Mathf.Clamp01(t / dur);
                float f = Mathf.Lerp(f0, f1, k);
                phase += 2f * Mathf.PI * f / SR;
                float s = Mathf.Sin(phase);
                s = Mathf.Sign(s) * 0.3f + s * 0.7f;
                if (noiseMix > 0f)
                {
                    float nz = Random.value * 2f - 1f;
                    s = s * (1f - noiseMix) + nz * noiseMix;
                }
                float env = Mathf.Exp(-decay * t) * Mathf.Min(1f, t * 600f);
                data[i] = s * env * gain;
            }
            FadeTail(data, Mathf.Min(0.008f, dur * 0.15f));
            return FromData(name, data);
        }

        static AudioClip Noise(string name, float dur, float decay, float gain, float lowpass)
        {
            int n = Mathf.Max(8, Mathf.CeilToInt(SR * dur));
            var data = new float[n];
            float lp = 0f;
            float a = Mathf.Clamp01(lowpass);
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float nz = Random.value * 2f - 1f;
                lp = lp + (nz - lp) * a;
                float env = Mathf.Exp(-decay * t) * Mathf.Min(1f, t * 900f);
                data[i] = lp * env * gain;
            }
            FadeTail(data, Mathf.Min(0.008f, dur * 0.15f));
            return FromData(name, data);
        }

        static AudioClip FromData(string name, float[] data)
        {
            var clip = AudioClip.Create(name, data.Length, 1, SR, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>一阶低通的系数：截止频率 fc 越低，输出越闷。</summary>
        static float Coef(float fc)
        {
            fc = Mathf.Clamp(fc, 10f, SR * 0.45f);
            return 1f - Mathf.Exp(-2f * Mathf.PI * fc / SR);
        }

        /// <summary>
        /// 低频爆炸。三层叠起来：
        ///   1) 正弦次低频，频率从 subF0 下扫到 subF1 —— 提供"沉"的底子；
        ///      subGain 越小越不像鼓：鼓点的本质就是一条**大幅度的快速下滑正弦**，
        ///      所以现在只给很轻的权重、而且只做小幅下扫（如 70→55），听感从"咚"变成"轰"；
        ///   2) 白噪过两级低通（12dB/oct），截止频率同步下扫 —— 闷响的"肉"，这是主体；
        ///   3) 极短的高通噪声（crack，可为 0）—— 只提供爆炸瞬间的轮廓，量大就会变脆。
        /// </summary>
        static AudioClip Blast(string name, float dur, float subF0, float subF1, float subDecay, float subGain,
                               float bodyDecay, float cut0, float cut1, float crack, float bodyGain, float peak)
        {
            int n = Mathf.Max(8, Mathf.CeilToInt(SR * dur));
            var data = new float[n];
            float phase = 0f;
            float lp1 = 0f, lp2 = 0f, hp = 0f;
            float crackA = Coef(1200f);

            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float k = Mathf.Clamp01(t / dur);

                float s = 0f;

                // 1) 次低频
                if (subGain > 0f)
                {
                    float f = Mathf.Lerp(subF0, subF1, k * k);
                    phase += 2f * Mathf.PI * f / SR;
                    float sub = Mathf.Sin(phase);
                    sub = sub / (1f + Mathf.Abs(sub) * 0.6f);      // 轻微软饱和，增加厚度
                    s = sub * Mathf.Exp(-subDecay * t) * Mathf.Min(1f, t * 400f) * subGain;
                }

                // 2) 闷响本体：两级低通噪声
                float a = Coef(Mathf.Lerp(cut0, cut1, Mathf.Sqrt(k)));
                float nz = Random.value * 2f - 1f;
                lp1 += (nz - lp1) * a;
                lp2 += (lp1 - lp2) * a;
                s += lp2 * Mathf.Exp(-bodyDecay * t) * Mathf.Min(1f, t * 900f) * bodyGain;

                // 3) 打击感
                if (crack > 0f)
                {
                    hp += (nz - hp) * crackA;
                    s += (nz - hp) * Mathf.Exp(-60f * t) * crack;
                }

                data[i] = s;
            }

            FadeTail(data, 0.10f);
            Normalize(data, peak);
            return FromData(name, data);
        }

        /// <summary>
        /// "扫过"声：白噪过**两级低通**（12dB/oct），截止频率从 cut0 上扫到 cut1，
        /// 再减掉一条更低截止的一阶低通（去掉多余的隆隆）。
        /// 和之前那版带通结构的区别：那个"带通"其实是高通，怎么降参都还是 6kHz 的嘶声；
        /// 现在能量真的集中在 cut0~cut1 附近（重心约 3kHz），听感是刷子扫过去。
        /// atk 是起音斜率的系数（越大越硬），刻意放软，避免任何"爆破"的爆点。
        /// </summary>
        static AudioClip Swish(string name, float dur, float cut0, float cut1, float decay, float atk, float peak)
        {
            int n = Mathf.Max(8, Mathf.CeilToInt(SR * dur));
            var data = new float[n];
            float lp1 = 0f, lp2 = 0f, low = 0f;

            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float k = Mathf.Clamp01(t / dur);

                float fc = Mathf.Lerp(cut0, cut1, k * k);
                float a = Coef(fc);
                float aLow = Coef(fc * 0.22f);

                float nz = Random.value * 2f - 1f;
                lp1 += (nz - lp1) * a;
                lp2 += (lp1 - lp2) * a;
                low += (nz - low) * aLow;

                float env = Mathf.Exp(-decay * t) * Mathf.Min(1f, t * atk);
                data[i] = (lp2 - low * 0.8f) * env;
            }

            FadeTail(data, 0.012f);
            Normalize(data, peak);
            return FromData(name, data);
        }

        /// <summary>
        /// 尾部线性淡出到 0。指数包络在片段结束处往往还剩 20%~30% 振幅，
        /// 直接截断会每发子弹/每次爆炸都带一声"啪"（直流跳变），必须收干净。
        /// </summary>
        static void FadeTail(float[] data, float seconds)
        {
            int fade = Mathf.Min(Mathf.CeilToInt(SR * seconds), data.Length / 4);
            for (int i = 0; i < fade; i++)
            {
                int idx = data.Length - fade + i;
                // 系数必须从 1 线性降到 0：i=0 时是淡出区的第一帧（保持原样），
                // 最后一个采样点才是 0。写成 i/fade 会把首尾对调，等于没淡出还在中间挖个坑。
                data[idx] *= (fade - i) / (float)fade;
            }
        }

        /// <summary>
        /// 风声：白噪过**4 级级联低通**（24dB/oct），截止频率从 cut0 收到 cut1。
        /// 为什么是 4 级：2 级低通的裙边太缓，4kHz 以上还能漏出 17% 的能量，听起来就是嘶声；
        /// 4 级能压到 0.5% 以下，才是"风"而不是"沙"。
        /// 包络刻意不用指数：起音用 smoothstep（十几毫秒），衰减用 (1-k)^fallPow，
        /// 两端都平滑收在 0，没有硬起音也就没有"啪"。
        /// </summary>
        static AudioClip Wind(string name, float dur, float cut0, float cut1,
                              float attack, float fallPow, float peak)
        {
            int n = Mathf.Max(8, Mathf.CeilToInt(SR * dur));
            var data = new float[n];
            float l0 = 0f, l1 = 0f, l2 = 0f, l3 = 0f;

            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float k = Mathf.Clamp01(t / dur);

                float a = Coef(Mathf.Lerp(cut0, cut1, k));
                float x = Random.value * 2f - 1f;
                l0 += (x - l0) * a;
                l1 += (l0 - l1) * a;
                l2 += (l1 - l2) * a;
                l3 += (l2 - l3) * a;

                float rise = Mathf.Clamp01(t / attack);
                rise = rise * rise * (3f - 2f * rise);      // smoothstep 起音
                float fall = Mathf.Pow(1f - k, fallPow);

                data[i] = l3 * rise * fall;
            }

            Normalize(data, peak);
            return FromData(name, data);
        }

        /// <summary>软限幅 + 峰值归一到 peak，保证不同音效之间的响度关系可控。</summary>
        static void Normalize(float[] data, float peak)
        {
            float max = 0f;
            for (int i = 0; i < data.Length; i++)
            {
                float v = data[i];
                v = v / (1f + Mathf.Abs(v) * 0.35f);
                data[i] = v;
                float a = Mathf.Abs(v);
                if (a > max) max = a;
            }
            float g = peak / Mathf.Max(max, 1e-5f);
            for (int i = 0; i < data.Length; i++) data[i] *= g;
        }

        // ---------------- 背景音乐 ----------------

        /// <summary>
        /// 程序化生成一段 16 秒循环的合成器音轨。
        /// A 小调 i-VI-III-VII 走向：底鼓 / 反拍军鼓 / 踩镲 / 8 分贝斯 / 16 分琶音 / 铺底。
        /// </summary>
        static AudioClip MakeMusic()
        {
            float[] data = MusicSamples();
            var clip = AudioClip.Create("bgm", data.Length, 1, SR, false);
            clip.SetData(data, 0);
            return clip;
        }

        /// <summary>只生成波形数据（不建 AudioClip），方便离线校验。</summary>
        static float[] MusicSamples()
        {
            const float bpm = 120f;
            const int bars = 8;
            const float beat = 60f / bpm;
            const float twoPi = Mathf.PI * 2f;

            int n = Mathf.CeilToInt(bars * 4f * beat * SR);
            var data = new float[n];

            // i - VI - III - VII（A 小调）
            float[] roots = { 110.00f, 87.31f, 130.81f, 98.00f };
            float[] third = { 3f, 4f, 4f, 4f };      // 三度音的半音数

            // 预先算好每个和弦的四个琶音音程比，避免逐采样 Pow
            var arpRatio = new float[4, 4];
            for (int b = 0; b < 4; b++)
            {
                arpRatio[b, 0] = 1f;
                arpRatio[b, 1] = Mathf.Pow(2f, third[b] / 12f);
                arpRatio[b, 2] = Mathf.Pow(2f, 7f / 12f);
                arpRatio[b, 3] = 2f;
            }
            float fifthRatio = Mathf.Pow(2f, 7f / 12f);

            for (int i = 0; i < n; i++)
            {
                float t = i / (float)SR;
                float beatPos = t / beat;
                int bar = Mathf.FloorToInt(beatPos / 4f) % roots.Length;
                int step16 = Mathf.FloorToInt(beatPos * 4f);
                float ts = (beatPos * 4f - step16) * beat * 0.25f;   // 当前 16 分内经过的秒数

                float root = roots[bar];
                float s = 0f;

                // 底鼓：每拍一下
                if (step16 % 4 == 0)
                {
                    s += Mathf.Sin(ts * 55f * twoPi) * Mathf.Exp(-16f * ts) * 0.60f;
                    s += (Random.value * 2f - 1f) * Mathf.Exp(-70f * ts) * 0.16f;
                }

                // 反拍军鼓
                if (step16 % 8 == 4)
                {
                    s += (Random.value * 2f - 1f) * Mathf.Exp(-24f * ts) * 0.20f;
                }

                // 踩镲：8 分反拍
                if (step16 % 4 == 2)
                {
                    s += (Random.value * 2f - 1f) * Mathf.Exp(-48f * ts) * 0.09f;
                }

                // 贝斯：8 分音符
                if (step16 % 2 == 0)
                {
                    int bs = (step16 / 2) % 4;
                    float f = root * (bs == 3 ? 2f : 1f);
                    float osc = Mathf.Sin(t * f * twoPi);
                    osc = osc * 0.6f + Mathf.Sign(osc) * 0.4f;
                    s += osc * Mathf.Exp(-5.5f * ts) * 0.20f;
                }

                // 琶音：16 分音符
                {
                    int idx = step16 % 4;
                    float f = root * 4f * arpRatio[bar, idx];
                    s += Mathf.Sin(t * f * twoPi) * Mathf.Exp(-13f * ts) * 0.085f;
                }

                // 铺底
                {
                    float f = root * 2f;
                    s += Mathf.Sin(t * f * twoPi) * 0.055f;
                    s += Mathf.Sin(t * f * fifthRatio * twoPi) * 0.038f;
                }

                data[i] = s;
            }

            // 软限幅 + 首尾淡入淡出，避免循环接缝处爆音
            int fade = Mathf.Min(SR / 8, n / 4);
            for (int i = 0; i < n; i++)
            {
                float v = data[i];
                v = v / (1f + Mathf.Abs(v));
                if (i < fade) v *= i / (float)fade;
                else if (i >= n - fade) v *= (n - i) / (float)fade;
                data[i] = v * 0.9f;
            }

            return data;
        }
    }
}
