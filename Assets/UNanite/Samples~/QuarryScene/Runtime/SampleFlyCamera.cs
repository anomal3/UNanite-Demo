using UnityEngine;

namespace UNanite.Samples.QuarryScene
{
    /// <summary>
    /// Free-fly camera of the sample: hold the right mouse button to look, WASD to move, Q / E down / up,
    /// Shift faster, mouse wheel changes the speed.
    /// </summary>
    public sealed class SampleFlyCamera : MonoBehaviour
    {
        public float speed = 8f;
        public float lookSensitivity = 0.12f;

        float m_Yaw, m_Pitch;

        void OnEnable()
        {
            var e = transform.eulerAngles;
            m_Yaw = e.y;
            m_Pitch = e.x > 180f ? e.x - 360f : e.x;
        }

        void Update()
        {
            if (!ReadInput(out var look, out var move, out bool fast, out float scroll))
                return;
            if (scroll != 0f)
                speed = Mathf.Clamp(speed * Mathf.Pow(1.2f, scroll), 0.5f, 500f);
            m_Yaw += look.x * lookSensitivity;
            m_Pitch = Mathf.Clamp(m_Pitch - look.y * lookSensitivity, -89f, 89f);
            transform.rotation = Quaternion.Euler(m_Pitch, m_Yaw, 0f);
            transform.position += transform.rotation * move * (speed * (fast ? 4f : 1f) * Time.unscaledDeltaTime);
        }

        static bool ReadInput(out Vector2 look, out Vector3 move, out bool fast, out float scroll)
        {
            look = Vector2.zero;
            move = Vector3.zero;
            fast = false;
            scroll = 0f;
#if UNANITE_SAMPLE_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
            var kb = UnityEngine.InputSystem.Keyboard.current;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (kb == null || mouse == null)
                return false;
            if (mouse.rightButton.isPressed)
                look = mouse.delta.ReadValue();
            scroll = Mathf.Sign(mouse.scroll.ReadValue().y) * (mouse.scroll.ReadValue().y != 0f ? 1f : 0f);
            move = new Vector3(Axis(kb.dKey.isPressed, kb.aKey.isPressed), Axis(kb.eKey.isPressed, kb.qKey.isPressed), Axis(kb.wKey.isPressed, kb.sKey.isPressed));
            fast = kb.leftShiftKey.isPressed;
            return true;
#elif ENABLE_LEGACY_INPUT_MANAGER
            if (Input.GetMouseButton(1))
                look = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 20f;
            scroll = Input.mouseScrollDelta.y;
            move = new Vector3(Axis(Input.GetKey(KeyCode.D), Input.GetKey(KeyCode.A)), Axis(Input.GetKey(KeyCode.E), Input.GetKey(KeyCode.Q)),
                               Axis(Input.GetKey(KeyCode.W), Input.GetKey(KeyCode.S)));
            fast = Input.GetKey(KeyCode.LeftShift);
            return true;
#else
            return false;
#endif
        }

        static float Axis(bool positive, bool negative) => (positive ? 1f : 0f) - (negative ? 1f : 0f);
    }
}
