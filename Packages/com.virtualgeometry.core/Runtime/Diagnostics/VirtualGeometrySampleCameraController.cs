using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

namespace VirtualizedGeometry
{
    /// <summary>
    /// sample 用のカメラ操作。Game View にフォーカスがある Play Mode で、
    /// W/S 前後、A/D 左右、Q/E 上下に移動し、右ボタンを押している間はマウスで向きを変える。
    /// frameOnStart が true なら、開始時に VirtualGeometryRenderer の範囲全体が画面に入る位置へカメラを置く。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class VirtualGeometrySampleCameraController : MonoBehaviour
    {
        [SerializeField, Min(0f)] private float moveSpeed = 5f;
        [SerializeField, Min(1f)] private float fastMultiplier = 3f;
        [SerializeField, Min(0f)] private float lookSensitivity = 0.15f;
        [SerializeField] private bool frameOnStart;

        private float yaw;
        private float pitch;
        private bool lookInitialized;
        private bool looking;
        private bool framed;
        private int frameAttempts;
        private const int MaximumFrameAttempts = 120;

        /// <summary>範囲全体が垂直画角に収まる、範囲の中心から後ろへの距離(球で近似)。</summary>
        public static float FramingDistance(Bounds bounds, float verticalFieldOfViewDegrees)
        {
            float radius = bounds.extents.magnitude;
            float half = Mathf.Clamp(verticalFieldOfViewDegrees, 1f, 170f) * 0.5f * Mathf.Deg2Rad;
            return radius / Mathf.Sin(half);
        }

        /// <summary>向きを forward にしたまま、範囲全体が見える位置。</summary>
        public static Vector3 FramingPosition(Bounds bounds, float verticalFieldOfViewDegrees, Vector3 forward)
        {
            return bounds.center - forward.normalized * FramingDistance(bounds, verticalFieldOfViewDegrees);
        }

        /// <summary>pitch を上下 89° に制限する。</summary>
        public static float ClampPitch(float degrees)
        {
            return Mathf.Clamp(degrees, -89f, 89f);
        }

        private void Start()
        {
            this.InitializeLook();
        }

        private void InitializeLook()
        {
            Vector3 euler = this.transform.eulerAngles;
            this.yaw = euler.y;
            this.pitch = euler.x > 180f ? euler.x - 360f : euler.x;
            this.lookInitialized = true;
        }

        /// <summary>VirtualGeometryRenderer の範囲全体が画面に入る位置へカメラを置く。範囲がまだ空なら false。</summary>
        [ContextMenu("Frame Virtual Geometry Renderers")]
        public bool Frame()
        {
            Camera camera = this.GetComponent<Camera>();
            VirtualGeometryRenderer[] renderers = FindObjectsByType<VirtualGeometryRenderer>(FindObjectsSortMode.None);
            bool any = false;
            Bounds total = new Bounds();
            foreach (VirtualGeometryRenderer renderer in renderers)
            {
                if (!renderer.isActiveAndEnabled || renderer.MeshAsset == null)
                {
                    continue;
                }

                Bounds bounds = renderer.WorldBounds;
                if (!any)
                {
                    total = bounds;
                    any = true;
                }
                else
                {
                    total.Encapsulate(bounds);
                }
            }

            if (!any || total.extents.sqrMagnitude <= 0f)
            {
                return false;
            }

            float fov = camera != null ? camera.fieldOfView : 60f;
            // 範囲の斜め上から見る(真横より立体が分かる)。
            Vector3 forward = Quaternion.Euler(20f, 30f, 0f) * Vector3.forward;
            this.transform.position = FramingPosition(total, fov, forward);
            this.transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
            this.InitializeLook();
            return true;
        }

        private void Update()
        {
            if (!Application.isPlaying)
            {
                return;
            }

            if (this.frameOnStart && !this.framed && this.frameAttempts < MaximumFrameAttempts)
            {
                // 変換データの読込を待つ間は範囲が空なので、数 frame 試す。
                this.frameAttempts++;
                this.framed = this.Frame();
            }

            if (!this.lookInitialized)
            {
                this.InitializeLook();
            }

            float horizontal;
            float vertical;
            float depth;
            bool fast;
            Vector2 lookDelta = Vector2.zero;
            bool lookHeld = false;

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
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                lookHeld = mouse.rightButton.isPressed;
                lookDelta = mouse.delta.ReadValue();
            }
#else
            horizontal = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
            vertical = (Input.GetKey(KeyCode.E) ? 1f : 0f) - (Input.GetKey(KeyCode.Q) ? 1f : 0f);
            depth = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
            fast = Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift);
            lookHeld = Input.GetMouseButton(1);
            lookDelta = new Vector2(Input.GetAxisRaw("Mouse X"), Input.GetAxisRaw("Mouse Y")) * 10f;
#endif

            this.UpdateLook(lookHeld, lookDelta);

            Vector3 direction = transform.right * horizontal + transform.up * vertical + transform.forward * depth;
            if (direction.sqrMagnitude > 1f)
            {
                direction.Normalize();
            }

            float speed = moveSpeed * (fast ? fastMultiplier : 1f);
            transform.position += direction * speed * Time.unscaledDeltaTime;
        }

        // 右ボタンを押している間だけ向きを変え、カーソルを隠してロックする(離したら戻す)。
        private void UpdateLook(bool held, Vector2 delta)
        {
            if (held && !this.looking)
            {
                this.looking = true;
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            else if (!held && this.looking)
            {
                this.looking = false;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }

            if (!held)
            {
                return;
            }

            this.yaw += delta.x * this.lookSensitivity;
            this.pitch = ClampPitch(this.pitch - delta.y * this.lookSensitivity);
            this.transform.rotation = Quaternion.Euler(this.pitch, this.yaw, 0f);
        }

        private void OnDisable()
        {
            if (this.looking)
            {
                this.looking = false;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
        }
    }
}
