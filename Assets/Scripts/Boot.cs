using UnityEngine;
using UnityEngine.Rendering;

namespace GeoClash
{
    /// <summary>
    /// 自动启动器：进入 Play 模式后在任意场景里把游戏搭起来，
    /// 所以不需要手动拖拽任何预制体或组件。
    /// </summary>
    public static class Boot
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Init()
        {
            if (Object.FindFirstObjectByType<Game>() != null) return;

            // 清掉模板场景自带的相机 / 灯光 / 后处理，避免和游戏内的一套冲突
            var cams = Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            for (int i = 0; i < cams.Length; i++)
            {
                if (cams[i] == null) continue;
                cams[i].gameObject.SetActive(false);
                Object.Destroy(cams[i].gameObject);
            }

            var lights = Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i] == null) continue;
                lights[i].gameObject.SetActive(false);
                Object.Destroy(lights[i].gameObject);
            }

            var volumes = Object.FindObjectsByType<Volume>(FindObjectsSortMode.None);
            for (int i = 0; i < volumes.Length; i++)
            {
                if (volumes[i] == null) continue;
                volumes[i].gameObject.SetActive(false);
                Object.Destroy(volumes[i].gameObject);
            }

            var go = new GameObject("GeoClash");
            go.AddComponent<Game>();
        }
    }
}
