using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 镜头导演：控制相机在不同构图点之间平滑移动。
    ///
    /// 只做"看船 → 岛停靠后看岛"，不做拖动/边界/Cinemachine。
    /// </summary>
    public class CameraDirector : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("主相机")] public Camera MainCamera;
        [Tooltip("船只镜头构图点")] public Transform ShipCameraPoint;

        [Header("移动参数")]
        [Tooltip("镜头移动速度")] public float MoveSpeed = 3f;
        [Tooltip("到达判定距离")] public float ArrivalThreshold = 0.05f;

        // 相机原始 Z 值，全程保持不变
        private float _originalZ;

        // 当前目标构图点（null 表示不移动）
        private Transform _targetPoint;
        private bool _isMoving;

        /// <summary>岛屿停靠后的回调目标</summary>
        public event System.Action OnCameraArrived;

        private void Start()
        {
            if (MainCamera == null)
                MainCamera = Camera.main;

            // 记住相机 Z 值
            _originalZ = MainCamera.transform.position.z;

            // 游戏开始，立刻对准 ShipCameraPoint
            if (ShipCameraPoint != null)
            {
                SnapTo(ShipCameraPoint);
                Debug.Log("[CameraDirector] 初始对准 ShipCameraPoint");
            }
        }

        private void Update()
        {
            if (!_isMoving || _targetPoint == null) return;

            Vector3 camPos = MainCamera.transform.position;
            Vector2 targetXY = _targetPoint.position;

            // 只移动 X/Y
            Vector2 currentXY = new Vector2(camPos.x, camPos.y);
            Vector2 toTarget = targetXY - currentXY;

            float dist = toTarget.magnitude;
            if (dist <= ArrivalThreshold)
            {
                // 到达
                MainCamera.transform.position = new Vector3(targetXY.x, targetXY.y, _originalZ);
                _isMoving = false;
                Debug.Log($"[CameraDirector] 镜头已到达 {_targetPoint.name}");
                OnCameraArrived?.Invoke();
                return;
            }

            // 平滑移动
            Vector2 dir = toTarget.normalized;
            Vector2 newXY = currentXY + dir * MoveSpeed * Time.deltaTime;
            MainCamera.transform.position = new Vector3(newXY.x, newXY.y, _originalZ);
        }

        /// <summary>立刻对准某个构图点（无动画）</summary>
        private void SnapTo(Transform point)
        {
            Vector3 pos = MainCamera.transform.position;
            pos.x = point.position.x;
            pos.y = point.position.y;
            // Z 保持不变
            pos.z = _originalZ;
            MainCamera.transform.position = pos;
        }

        /// <summary>
        /// 平滑移动到指定构图点。
        /// 由 IslandEncounterController 在岛屿停靠后调用。
        /// </summary>
        public void MoveTo(Transform cameraPoint)
        {
            if (cameraPoint == null)
            {
                Debug.LogWarning("[CameraDirector] MoveTo 传入了 null");
                return;
            }

            _targetPoint = cameraPoint;
            _isMoving = true;
            Debug.Log($"[CameraDirector] 开始移动到 {cameraPoint.name}");
        }
    }
}
