using UnityEngine;
using UnityEngine.InputSystem;

namespace GeoClash
{
    /// <summary>
    /// 输入封装：同时支持 键鼠 与 手柄。
    /// 移动 = WASD / 左摇杆；瞄准 = 鼠标 / 右摇杆；开火 = 左键 / 右扳机 / 右摇杆。
    /// </summary>
    public static class Controls
    {
        public static Vector2 Move()
        {
            Vector2 v = Vector2.zero;
            var kb = Keyboard.current;
            if (kb != null)
            {
                if (kb.wKey.isPressed || kb.upArrowKey.isPressed) v.y += 1f;
                if (kb.sKey.isPressed || kb.downArrowKey.isPressed) v.y -= 1f;
                if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) v.x -= 1f;
                if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) v.x += 1f;
            }

            var pad = Gamepad.current;
            if (pad != null)
            {
                var s = pad.leftStick.ReadValue();
                if (s.sqrMagnitude > v.sqrMagnitude) v = s;
            }

            return v.sqrMagnitude > 1f ? v.normalized : v;
        }

        public static Vector2 Aim(Vector3 from)
        {
            var pad = Gamepad.current;
            if (pad != null)
            {
                var s = pad.rightStick.ReadValue();
                if (s.magnitude > 0.35f) return s.normalized;
            }

            var mouse = Mouse.current;
            var cam = Game.I != null ? Game.I.Cam : null;
            if (mouse != null && cam != null)
            {
                Vector2 sp = mouse.position.ReadValue();
                Ray ray = cam.ScreenPointToRay(new Vector3(sp.x, sp.y, 0f));
                // 游戏平面在 z=0 上，法线是 +Z
                var plane = new Plane(Vector3.forward, Vector3.zero);
                float d;
                if (plane.Raycast(ray, out d))
                {
                    Vector3 hit = ray.GetPoint(d);
                    Vector2 dir = new Vector2(hit.x - from.x, hit.y - from.y);
                    if (dir.magnitude > 0.35f) return dir.normalized;
                }
            }

            return Vector2.up;
        }

        public static bool FireHeld()
        {
            var mouse = Mouse.current;
            if (mouse != null && mouse.leftButton.isPressed) return true;

            var pad = Gamepad.current;
            if (pad != null)
            {
                if (pad.rightTrigger.ReadValue() > 0.4f) return true;
                if (pad.rightStick.ReadValue().magnitude > 0.6f) return true;
            }
            return false;
        }

        public static bool DashPressed()
        {
            var kb = Keyboard.current;
            if (kb != null && (kb.leftShiftKey.wasPressedThisFrame || kb.rightShiftKey.wasPressedThisFrame)) return true;
            var pad = Gamepad.current;
            if (pad != null && pad.rightShoulder.wasPressedThisFrame) return true;
            return false;
        }

        public static bool BombPressed()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.spaceKey.wasPressedThisFrame) return true;
            var pad = Gamepad.current;
            if (pad != null && pad.leftShoulder.wasPressedThisFrame) return true;
            return false;
        }

        public static bool PausePressed()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.escapeKey.wasPressedThisFrame) return true;
            var pad = Gamepad.current;
            if (pad != null && (pad.startButton.wasPressedThisFrame || pad.selectButton.wasPressedThisFrame)) return true;
            return false;
        }

        public static bool MutePressed()
        {
            var kb = Keyboard.current;
            return kb != null && kb.mKey.wasPressedThisFrame;
        }

        public static bool StartPressed()
        {
            var kb = Keyboard.current;
            if (kb != null && kb.anyKey.wasPressedThisFrame) return true;
            var pad = Gamepad.current;
            if (pad != null && (pad.buttonSouth.wasPressedThisFrame || pad.startButton.wasPressedThisFrame)) return true;
            return false;
        }
    }
}
