using UnityEngine;
using UnityEngine.UI;

namespace GeoClash
{
    /// <summary>HUD：分数 / 倍率 / 波次 / 生命 / 炸弹 / BOSS 血条 / 标题与结算界面。</summary>
    public class Hud : MonoBehaviour
    {
        Canvas canvas;
        Font font;

        Text scoreLabel, scoreText;
        Text multText;
        Text infoText;
        Text centerText;
        Text bossLabel;
        RawImage bossBg, bossFill;

        // 得分飘字池
        const int PopupMax = 28;
        const float PopupDur = 0.5f;
        Text[] popupTexts;
        Vector3[] popupWorld;
        float[] popupLife;
        float[] popupScale;
        int popupCursor;

        GameObject hudGroup, titleGroup, overGroup, pauseGroup;
        Text titleText, titleHint;
        Text overTitle, overScore, overHint;
        Text pauseText;

        float centerTimer;
        Color centerColor = Color.white;
        string centerMsg = "";

        int lastScore = -1;
        int lastLives = -1;
        int lastBombs = -1;
        int lastWave = -1;
        int lastMult100 = -1;
        float multPulse;

        // ------------------------------------------------------------ 构建

        void Awake()
        {
            font = PickFont();

            var canvasGo = new GameObject("Canvas", typeof(RectTransform));
            canvasGo.transform.SetParent(transform, false);
            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            BuildHud();
            BuildTitle();
            BuildGameOver();
            BuildPause();
            BuildPopups();
        }

        void BuildPopups()
        {
            popupTexts = new Text[PopupMax];
            popupWorld = new Vector3[PopupMax];
            popupLife = new float[PopupMax];
            popupScale = new float[PopupMax];

            for (int i = 0; i < PopupMax; i++)
            {
                var t = MakeText(canvas.transform, "Popup" + i, new Vector2(0.5f, 0.5f), Vector2.zero,
                                 30, TextAnchor.MiddleCenter, Color.white);
                t.rectTransform.sizeDelta = new Vector2(320f, 60f);
                t.enabled = false;
                popupTexts[i] = t;
            }
        }

        /// <summary>在世界坐标处弹出一个得分飘字（已乘倍率的实际得分），0.5 秒淡出。</summary>
        public void ScorePopup(Vector3 worldPos, int value, float scale)
        {
            if (popupTexts == null) return;

            int i = popupCursor;
            popupCursor = (popupCursor + 1) % PopupMax;

            popupWorld[i] = worldPos + Vector3.up * 0.6f;
            popupLife[i] = PopupDur;
            popupScale[i] = Mathf.Max(0.6f, scale);
            popupTexts[i].text = "+" + value;
            popupTexts[i].color = Color.white;
            popupTexts[i].enabled = true;
        }

        void UpdatePopups(float dt)
        {
            if (popupTexts == null || Game.I == null || Game.I.Cam == null) return;

            Camera cam = Game.I.Cam;

            for (int i = 0; i < PopupMax; i++)
            {
                if (popupLife[i] <= 0f) continue;

                popupLife[i] -= dt;
                var t = popupTexts[i];

                if (popupLife[i] <= 0f)
                {
                    t.enabled = false;
                    continue;
                }

                // 缓慢上飘
                popupWorld[i] += Vector3.up * (2.4f * dt);

                Vector3 sp = cam.WorldToScreenPoint(popupWorld[i]);
                if (sp.z <= 0f)
                {
                    t.enabled = false;
                    continue;
                }

                float k = popupLife[i] / PopupDur;          // 1 -> 0
                t.rectTransform.position = sp;
                t.color = new Color(1f, 1f, 1f, Mathf.Clamp01(k * 2.2f));

                // 冒出时略微放大再收回
                float pop = 1f + 0.45f * Mathf.Pow(k, 5f);
                t.rectTransform.localScale = Vector3.one * (pop * popupScale[i]);
            }
        }

        /// <summary>
        /// UI 全英文，所以优先用 Unity 内置字体——它一定会被打进包，
        /// WebGL 里也能正常显示（系统字体在浏览器沙箱里取不到）。
        /// </summary>
        static Font PickFont()
        {
            Font f = null;
            try { f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); } catch (System.Exception) { }
            if (f == null)
            {
                try { f = Resources.GetBuiltinResource<Font>("Arial.ttf"); } catch (System.Exception) { }
            }
            if (f == null)
            {
                try { f = Font.CreateDynamicFontFromOSFont(new[] { "Arial", "Segoe UI", "Helvetica" }, 48); }
                catch (System.Exception) { }
            }
            return f;
        }

        Text MakeText(Transform parent, string name, Vector2 anchor, Vector2 pos, int size, TextAnchor align, Color col, bool outline = true)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var t = go.AddComponent<Text>();
            t.font = font;
            t.fontSize = size;
            t.alignment = align;
            t.color = col;
            t.fontStyle = FontStyle.Bold;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.supportRichText = true;

            var rt = t.rectTransform;
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = anchor;
            rt.anchoredPosition = pos;
            rt.sizeDelta = new Vector2(900f, 160f);

            if (outline)
            {
                var o = go.AddComponent<Outline>();
                o.effectColor = new Color(col.r * 0.7f, col.g * 0.7f, col.b * 0.7f, 0.45f);
                o.effectDistance = new Vector2(2.5f, -2.5f);
            }
            return t;
        }

        void BuildHud()
        {
            hudGroup = new GameObject("HudGroup", typeof(RectTransform));
            hudGroup.transform.SetParent(canvas.transform, false);
            var rt = hudGroup.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            scoreLabel = MakeText(hudGroup.transform, "ScoreLabel", new Vector2(0f, 1f), new Vector2(56f, -38f), 22, TextAnchor.UpperLeft, Palette.UiDim);
            scoreLabel.text = "SCORE";

            scoreText = MakeText(hudGroup.transform, "Score", new Vector2(0f, 1f), new Vector2(52f, -66f), 52, TextAnchor.UpperLeft, Color.white);

            multText = MakeText(hudGroup.transform, "Mult", new Vector2(0.5f, 1f), new Vector2(0f, -34f), 58, TextAnchor.UpperCenter, Palette.UiCyan);

            infoText = MakeText(hudGroup.transform, "Info", new Vector2(1f, 1f), new Vector2(-52f, -38f), 24, TextAnchor.UpperRight, Palette.UiDim);

            centerText = MakeText(hudGroup.transform, "Center", new Vector2(0.5f, 0.5f), new Vector2(0f, 90f), 78, TextAnchor.MiddleCenter, Color.white);

            // BOSS 血条
            var bgGo = new GameObject("BossBg", typeof(RectTransform));
            bgGo.transform.SetParent(hudGroup.transform, false);
            bossBg = bgGo.AddComponent<RawImage>();
            bossBg.texture = Texture2D.whiteTexture;
            bossBg.color = new Color(0.9f, 0.2f, 0.4f, 0.16f);
            bossBg.raycastTarget = false;
            var bgRt = bossBg.rectTransform;
            bgRt.anchorMin = new Vector2(0.5f, 1f);
            bgRt.anchorMax = new Vector2(0.5f, 1f);
            bgRt.pivot = new Vector2(0.5f, 1f);
            bgRt.anchoredPosition = new Vector2(0f, -118f);
            bgRt.sizeDelta = new Vector2(620f, 10f);

            var fillGo = new GameObject("BossFill", typeof(RectTransform));
            fillGo.transform.SetParent(bgGo.transform, false);
            bossFill = fillGo.AddComponent<RawImage>();
            bossFill.texture = Texture2D.whiteTexture;
            bossFill.color = new Color(1f, 0.35f, 0.65f, 1f);
            bossFill.raycastTarget = false;
            var fRt = bossFill.rectTransform;
            fRt.anchorMin = Vector2.zero;
            fRt.anchorMax = Vector2.one;
            fRt.offsetMin = Vector2.zero;
            fRt.offsetMax = Vector2.zero;

            bossLabel = MakeText(hudGroup.transform, "BossLabel", new Vector2(0.5f, 1f), new Vector2(0f, -132f), 20, TextAnchor.UpperCenter, new Color(1f, 0.45f, 0.7f));
            bossLabel.text = "BOSS";

            bossBg.gameObject.SetActive(false);
            bossLabel.gameObject.SetActive(false);
            centerText.enabled = false;
        }

        void BuildTitle()
        {
            titleGroup = new GameObject("TitleGroup", typeof(RectTransform));
            titleGroup.transform.SetParent(canvas.transform, false);
            var rt = titleGroup.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            titleText = MakeText(titleGroup.transform, "Title", new Vector2(0.5f, 0.5f), new Vector2(0f, 190f), 104, TextAnchor.MiddleCenter, Palette.UiCyan);
            titleText.text = "GEOMETRY CLASH";

            titleHint = MakeText(titleGroup.transform, "Hint", new Vector2(0.5f, 0.5f), new Vector2(0f, -80f), 26, TextAnchor.MiddleCenter, Palette.UiDim);
            titleHint.text =
                "[WASD] / [LEFT STICK]        MOVE\n" +
                "[MOUSE] / [RIGHT STICK]      AIM & FIRE\n" +
                "[SHIFT]  DASH     [SPACE]  BOMB     [ESC]  PAUSE     [M]  MUTE\n\n" +
                "<color=#8FE8FF>PRESS ANY KEY TO START</color>";
        }

        void BuildGameOver()
        {
            overGroup = new GameObject("OverGroup", typeof(RectTransform));
            overGroup.transform.SetParent(canvas.transform, false);
            var rt = overGroup.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            overTitle = MakeText(overGroup.transform, "OverTitle", new Vector2(0.5f, 0.5f), new Vector2(0f, 140f), 96, TextAnchor.MiddleCenter, new Color(1f, 0.4f, 0.5f));
            overTitle.text = "GAME OVER";

            overScore = MakeText(overGroup.transform, "OverScore", new Vector2(0.5f, 0.5f), new Vector2(0f, 20f), 40, TextAnchor.MiddleCenter, Color.white);

            overHint = MakeText(overGroup.transform, "OverHint", new Vector2(0.5f, 0.5f), new Vector2(0f, -110f), 26, TextAnchor.MiddleCenter, Palette.UiDim);
            overHint.text = "<color=#8FE8FF>PRESS ANY KEY TO RESTART</color>";

            overGroup.SetActive(false);
        }

        void BuildPause()
        {
            pauseGroup = new GameObject("PauseGroup", typeof(RectTransform));
            pauseGroup.transform.SetParent(canvas.transform, false);
            var rt = pauseGroup.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            pauseText = MakeText(pauseGroup.transform, "Pause", new Vector2(0.5f, 0.5f), new Vector2(0f, 40f), 84, TextAnchor.MiddleCenter, Palette.UiCyan);
            pauseText.text = "PAUSED\n<size=26><color=#8AA0B0>PRESS [ESC] TO RESUME</color></size>";

            pauseGroup.SetActive(false);
        }

        // ------------------------------------------------------------ 对外

        public void Announce(string msg, float dur)
        {
            centerMsg = msg;
            centerTimer = dur;
            centerColor = Color.white;
            if (centerText != null)
            {
                centerText.text = msg;
                centerText.enabled = true;
            }
        }

        // ------------------------------------------------------------ 每帧

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            var g = Game.I;
            if (g == null) return;

            bool title = g.State == GameState.Title;
            bool over = g.State == GameState.GameOver;
            bool paused = g.State == GameState.Paused;

            if (hudGroup != null) hudGroup.SetActive(!title);
            if (titleGroup != null) titleGroup.SetActive(title);
            if (overGroup != null) overGroup.SetActive(over);
            if (pauseGroup != null) pauseGroup.SetActive(paused);

            if (!title)
            {
                if (lastScore != g.Score)
                {
                    lastScore = g.Score;
                    scoreText.text = g.Score.ToString("N0");
                }

                int m100 = Mathf.RoundToInt(g.Multiplier * 100f);
                if (m100 != lastMult100)
                {
                    if (lastMult100 >= 0 && m100 > lastMult100) multPulse = 1f;
                    lastMult100 = m100;
                    multText.text = "x" + (m100 / 100f).ToString("0.0");
                }

                if (lastWave != g.Wave || lastLives != g.Lives || lastBombs != g.Bombs)
                {
                    lastWave = g.Wave;
                    lastLives = g.Lives;
                    lastBombs = g.Bombs;
                    infoText.text = "WAVE " + Mathf.Max(0, g.Wave).ToString("00") + "\n"
                                    + (g.InfiniteLives ? InfiniteShips() : Ships(g.Lives)) + "\n"
                                    + BombsStr(g.Bombs);
                }

                float hp = g.BossHp01;
                bool hasBoss = hp >= 0f;
                if (bossBg != null) bossBg.gameObject.SetActive(hasBoss);
                if (bossLabel != null) bossLabel.gameObject.SetActive(hasBoss);
                if (hasBoss)
                {
                    bossFill.rectTransform.anchorMax = new Vector2(Mathf.Clamp01(hp), 1f);
                }

                if (over)
                {
                    overScore.text = "SCORE  " + g.Score.ToString("N0") + "        BEST  " + g.Best.ToString("N0");
                }
            }

            // 倍率脉冲
            multPulse = Mathf.Max(0f, multPulse - dt * 4f);
            Color mc = Color.Lerp(Palette.UiCyan, Color.white, multPulse);
            float scale = 1f + multPulse * 0.18f;
            if (multText != null)
            {
                multText.color = mc;
                multText.rectTransform.localScale = Vector3.one * scale;
            }

            UpdateCenter(dt);
            UpdatePopups(dt);

            // 标题呼吸
            if (title && titleText != null)
            {
                float k = Mathf.Sin(Time.unscaledTime * 2f) * 0.5f + 0.5f;
                titleText.color = Color.Lerp(new Color(0.25f, 0.75f, 1f), new Color(0.85f, 0.98f, 1f), k);
            }
        }

        static string Ships(int n)
        {
            if (n < 0) n = 0;
            return "<color=#7FE3FF>SHIP \u00D7" + n + "</color>";
        }

        static string InfiniteShips()
        {
            return "<color=#7FE3FF>SHIP \u00D7\u221E</color>";
        }

        static string BombsStr(int n)
        {
            if (n < 0) n = 0;
            return "<color=#FFC46B>BOMB \u00D7" + n + "</color>";
        }

        void UpdateCenter(float dt)
        {
            if (centerText == null) return;
            if (centerTimer <= 0f)
            {
                if (centerText.enabled) centerText.enabled = false;
                return;
            }

            centerTimer -= dt;
            float a = Mathf.Clamp01(centerTimer / 0.55f);
            centerText.enabled = true;
            centerText.color = new Color(centerColor.r, centerColor.g, centerColor.b, a);
            float s = 1f + Mathf.Clamp01(centerTimer - 1.4f) * 0.35f;
            centerText.rectTransform.localScale = Vector3.one * s;
            if (centerMsg.Length == 0) centerText.text = "";
        }
    }
}
