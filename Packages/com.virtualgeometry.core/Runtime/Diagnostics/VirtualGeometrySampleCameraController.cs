using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VirtualizedGeometry
{
    /// <summary>
    /// Practical sample の Game View 用カメラ移動。
    /// Game View にフォーカスがある Play Mode で、W/S 前後、A/D 左右、Q/E 上下に移動する。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VirtualGeometrySampleCameraController : MonoBehaviour
    {
        [SerializeField, Min(0f)] private float moveSpeed = 5f;
        [SerializeField, Min(1f)] private float fastMultiplier = 3f;

        private void Update()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            float horizontal;
            float vertical;
            float depth;
            bool fast;

#if ENABLE_INPUT_SYSTEM
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                return;
            }

            horizontal = (keyboard.dKey.isPressed ? 1f : 0f) - (keyboard.aKey.isPressed ? 1f : 0f);
            vertical = (keyboard.eKey.isPressed ? 1f : 0f) - (keyboard.qKey.isPressed ? 1f : 0f);
            depth = (keyboard.wKey.isPressed ? 1f : 0f) - (keyboard.sKey.isPressed ? 1f : 0f);
            fast = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
#else
            horizontal = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
            vertical = (Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f);
            depth = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
            fast = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
#endif

            Vector3 direction = transform.right * horizontal + transform.up * vertical + transform.forward * depth;
            if (direction.sqrMagnitude > 1f)
            {
                direction.Normalize();
            }

            float speed = moveSpeed * (fast ? fastMultiplier : 1f);
            transform.position += direction * speed * Time.unscaledDeltaTime;
        }
    }
}