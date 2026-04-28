using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 镜头导演：控制相机在不同构图点之间平滑移动，并在战斗阶段提供自由摄像头控制。
    ///
    /// 两种模式（互斥）：
    /// - 定向模式：镜头沿线性路径移向目标构图点（船只 ↔ 岛屿），独占 Camera
    /// - 自由模式：战斗阶段 WASD/方向键/鼠标拖拽平移 + 滚轮缩放，受 CameraBounds 约束
    ///
    /// 自由摄像头开启时机：镜头到达 IslandCameraPoint 后自动开启
    /// 自由摄像头关闭时机：战斗结束（OnBattleEnded）时自动关闭，镜头平滑回到 ShipCameraPoint
    /// </summary>
    public class CameraDirector : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("主相机")] public Camera MainCamera;
        [Tooltip("船只镜头构图点")] public Transform ShipCameraPoint;
        [Tooltip("战斗管理器（订阅战斗结束事件）")] public BattleManager BattleManagerRef;
        [Tooltip("滚轮缩放/拖拽生效区域（Canvas 上的 RectTransform，覆盖左侧战场区域，不覆盖右侧三消棋盘）")]
        public RectTransform CameraInputArea;

        [Header("定向移动参数")]
        [Tooltip("镜头移动速度")] public float MoveSpeed = 3f;
        [Tooltip("到达判定距离")] public float ArrivalThreshold = 0.05f;

        [Header("自由摄像头参数")]
        [Tooltip("WASD/方向键移动速度")] public float FreeMoveSpeed = 5f;
        [Tooltip("滚轮缩放速度")] public float ZoomSpeed = 2f;
        [Tooltip("最小正交尺寸（最大放大）")] public float MinZoom = 3f;
        [Tooltip("最大正交尺寸（最大缩小）")] public float MaxZoom = 10f;

        [Header("鼠标拖拽")]
        [Tooltip("是否启用鼠标拖拽平移")] public bool EnableMouseDrag = true;
        [Tooltip("拖拽移动速度系数")] public float DragSpeed = 1f;
        // 拖拽固定使用鼠标左键（button 0），不可配置

        [Header("默认缩放")]
        [Tooltip("默认正交尺寸（回船时恢复到此值，0 = 使用游戏开始时的 orthographicSize）")]
        public float DefaultZoom = 0f;

        [Header("调试")]
        [Tooltip("自由摄像头是否启用（只读）")]
        [SerializeField] private bool _freeCameraEnabled;

        // ── 定向模式状态 ──
        private float _originalZ;
        private Transform _targetPoint;
        private bool _isMoving;
        private bool _enableFreeCameraOnArrival;
        private bool _returningToShip;  // 标记当前是否在回船镜头移动中

        // ── 回船缩放恢复 ──
        private float _zoomStart;       // 回船开始时的 orthographicSize
        private float _zoomTarget;      // 回船目标 orthographicSize（= DefaultZoom）
        private float _zoomDuration;    // 回船缩放总时长
        private float _zoomElapsed;     // 回船缩放已用时间

        // ── 自由模式状态 ──
        // 存储 Collider2D 引用（不是 Bounds 值类型），每次 Clamp 时实时读取 bounds，
        // 这样 bounds 始终跟随岛屿当前位置
        private Collider2D _cameraBoundsCollider;

        // ── 鼠标拖拽状态 ──
        private bool _isDragging;
        private Vector3 _dragLastScreenPos;

        /// <summary>镜头到达目标构图点时触发</summary>
        public event System.Action OnCameraArrived;

        /// <summary>自由摄像头是否启用（只读）</summary>
        public bool IsFreeCameraEnabled => _freeCameraEnabled;

        // ───────────────────────── 生命周期 ─────────────────────────

        private void Start()
        {
            if (MainCamera == null)
                MainCamera = Camera.main;

            _originalZ = MainCamera.transform.position.z;

            // DefaultZoom = 0 表示使用游戏开始时的 orthographicSize
            if (DefaultZoom <= 0f)
                DefaultZoom = MainCamera.orthographicSize;

            if (ShipCameraPoint != null)
            {
                SnapTo(ShipCameraPoint);
                Debug.Log("[CameraDirector] 初始对准 ShipCameraPoint");
            }
        }

        private void OnEnable()
        {
            if (BattleManagerRef != null)
            {
                BattleManagerRef.OnBattleEnded += OnBattleEnded;
                Debug.Log("[CameraDirector] 已订阅 BattleManager.OnBattleEnded");
            }
        }

        private void OnDisable()
        {
            if (BattleManagerRef != null)
                BattleManagerRef.OnBattleEnded -= OnBattleEnded;
        }

        // ───────────────────────── 每帧更新 ─────────────────────────
        //
        // 优先级：自动镜头移动 > 自由镜头 > 什么都不做
        // 自动镜头移动期间，任何玩家输入（WASD/拖拽/滚轮）都不能改 Camera transform

        private void Update()
        {
            // ── 1. 自动镜头移动（最高优先级，独占 Camera） ──
            if (_isMoving && _targetPoint != null)
            {
                ProcessDirectedMovement();
                return;
            }

            // ── 2. 自由摄像头 ──
            if (_freeCameraEnabled)
            {
                // 双重保险：如果因某种原因 _isMoving 被设为 true，
                // HandleFreeCameraInput 内部会再次检查
                HandleFreeCameraInput();
                return;
            }

            // ── 3. 什么都不做 ──
        }

        // ───────────────────────── 自动镜头移动 ─────────────────────────

        /// <summary>
        /// 处理定向移动。自动移动期间独占 Camera，不允许任何玩家输入。
        /// 回船时同步恢复缩放到 DefaultZoom。
        /// </summary>
        private void ProcessDirectedMovement()
        {
            Vector3 camPos = MainCamera.transform.position;
            Vector2 targetXY = _targetPoint.position;

            Vector2 currentXY = new Vector2(camPos.x, camPos.y);
            Vector2 toTarget = targetXY - currentXY;

            float dist = toTarget.magnitude;

            // ── 回船时同步恢复缩放 ──
            if (_returningToShip && _zoomDuration > 0f)
            {
                _zoomElapsed += Time.deltaTime;
                float t = Mathf.Clamp01(_zoomElapsed / _zoomDuration);
                MainCamera.orthographicSize = Mathf.Lerp(_zoomStart, _zoomTarget, t);
            }

            if (dist <= ArrivalThreshold)
            {
                // 到达
                MainCamera.transform.position = new Vector3(targetXY.x, targetXY.y, _originalZ);
                _isMoving = false;

                // 回船完成：确保缩放精确到达目标值
                if (_returningToShip)
                {
                    MainCamera.orthographicSize = _zoomTarget;
                    _returningToShip = false;
                    Debug.Log("[Camera] 自动回船镜头完成");
                }
                else
                {
                    Debug.Log($"[Camera] 自动镜头移动完成 → {_targetPoint.name}");
                }

                // 到达后自动开启自由摄像头
                if (_enableFreeCameraOnArrival)
                {
                    _enableFreeCameraOnArrival = false;
                    EnableFreeCamera();
                }

                OnCameraArrived?.Invoke();
                return;
            }

            // 平滑移动
            Vector2 dir = toTarget.normalized;
            Vector2 newXY = currentXY + dir * MoveSpeed * Time.deltaTime;
            MainCamera.transform.position = new Vector3(newXY.x, newXY.y, _originalZ);
        }

        // ───────────────────────── 自由摄像头 ─────────────────────────

        /// <summary>
        /// 处理自由摄像头输入：WASD/方向键平移 + 鼠标拖拽平移 + 滚轮缩放。
        /// 自动镜头移动期间不会调用此方法（Update 中已 return）。
        /// </summary>
        private void HandleFreeCameraInput()
        {
            // 双重保险：自动移动期间不允许任何玩家输入改 Camera
            if (_isMoving) return;

            Vector3 camPos = MainCamera.transform.position;
            bool positionChanged = false;
            bool zoomChanged = false;

            // ── WASD / 方向键平移 ──
            float dx = 0f, dy = 0f;
            if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow))    dy += 1f;
            if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow))  dy -= 1f;
            if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow))  dx -= 1f;
            if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) dx += 1f;

            if (dx != 0f || dy != 0f)
            {
                Vector2 dir = new Vector2(dx, dy).normalized;
                camPos.x += dir.x * FreeMoveSpeed * Time.deltaTime;
                camPos.y += dir.y * FreeMoveSpeed * Time.deltaTime;
                positionChanged = true;
            }

            // ── 鼠标拖拽平移 ──
            if (EnableMouseDrag)
            {
                positionChanged |= HandleMouseDrag(ref camPos);
            }

            // ── 滚轮缩放 ──
            float scroll = Input.GetAxis("Mouse ScrollWheel");
            if (Mathf.Abs(scroll) > 0.001f)
            {
                bool inArea = IsMouseInInputArea();

                if (inArea)
                {
                    float newSize = MainCamera.orthographicSize - scroll * ZoomSpeed;
                    newSize = Mathf.Clamp(newSize, MinZoom, MaxZoom);
                    MainCamera.orthographicSize = newSize;
                    zoomChanged = true;
                }
            }

            // ── 位置/缩放变更后，钳制到 CameraBounds ──
            if ((positionChanged || zoomChanged) && _cameraBoundsCollider != null)
            {
                camPos = ClampToCameraBounds(camPos);
            }

            camPos.z = _originalZ;
            MainCamera.transform.position = camPos;
        }

        /// <summary>
        /// 处理鼠标左键拖拽平移。只在 CameraInputArea 内按下时生效。
        /// 使用屏幕坐标 delta 计算位移，避免摄像头移动影响鼠标世界坐标导致抖动。
        /// </summary>
        /// <returns>是否改变了摄像头位置</returns>
        private bool HandleMouseDrag(ref Vector3 camPos)
        {
            const int DragButton = 0; // 固定左键

            // 按下左键：仅在 CameraInputArea 内才启动拖拽
            if (Input.GetMouseButtonDown(DragButton))
            {
                if (IsMouseInInputArea())
                {
                    _isDragging = true;
                    _dragLastScreenPos = Input.mousePosition;
                }
                return false;
            }

            // 松开左键
            if (Input.GetMouseButtonUp(DragButton))
            {
                _isDragging = false;
                return false;
            }

            // 拖拽中：用屏幕坐标 delta 换算世界位移
            if (_isDragging && Input.GetMouseButton(DragButton))
            {
                Vector3 currentScreenPos = Input.mousePosition;
                Vector3 screenDelta = currentScreenPos - _dragLastScreenPos;

                // 屏幕像素 delta → 世界单位 delta
                // orthographicSize = 视口半高（世界单位），Screen.height = 视口全高（像素）
                float worldUnitsPerPixel = MainCamera.orthographicSize * 2f / Screen.height;
                Vector3 worldMove = -screenDelta * worldUnitsPerPixel * DragSpeed;

                if (worldMove.sqrMagnitude > 0.0001f)
                {
                    camPos.x += worldMove.x;
                    camPos.y += worldMove.y;
                    _dragLastScreenPos = currentScreenPos;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 检查鼠标屏幕坐标是否在 CameraInputArea 的 RectTransform 内。
        /// 根据 Canvas Render Mode 决定 eventCamera：
        /// - Screen Space - Overlay  → null
        /// - Screen Space - Camera   → canvas.worldCamera
        /// - World Space             → canvas.worldCamera
        /// </summary>
        private bool IsMouseInInputArea()
        {
            if (CameraInputArea == null)
            {
                Debug.LogWarning("[Camera] CameraInputArea 未设置");
                return false;
            }

            Canvas canvas = CameraInputArea.GetComponentInParent<Canvas>();
            if (canvas == null)
            {
                Debug.LogWarning("[Camera] CameraInputArea 的父级中没有 Canvas");
                return false;
            }

            // 根据 Canvas Render Mode 决定 eventCamera
            Camera eventCamera = null;
            if (canvas.renderMode == RenderMode.ScreenSpaceCamera
                || canvas.renderMode == RenderMode.WorldSpace)
            {
                eventCamera = canvas.worldCamera;
            }

            Vector2 mousePos = Input.mousePosition;
            Rect rectLocal = CameraInputArea.rect;
            bool result = RectTransformUtility.RectangleContainsScreenPoint(
                CameraInputArea, mousePos, eventCamera);

            // 调试日志：只在滚轮时输出（调用方已判断 scroll != 0）
            Debug.Log($"[Camera] 鼠标是否在 CameraInputArea = {result}"
                + $" | mousePos={mousePos}"
                + $" | rect={rectLocal}"
                + $" | canvasMode={canvas.renderMode}"
                + $" | eventCamera={(eventCamera != null ? eventCamera.name : "null")}");

            return result;
        }

        /// <summary>
        /// 将摄像头位置钳制在 CameraBounds 内，考虑当前正交尺寸。
        /// 每次实时读取 _cameraBoundsCollider.bounds，确保 bounds 跟随岛屿当前位置。
        /// </summary>
        private Vector3 ClampToCameraBounds(Vector3 camPos)
        {
            if (_cameraBoundsCollider == null) return camPos;

            // 实时读取 Collider2D.bounds（值类型，每次读取取当前值）
            Bounds bounds = _cameraBoundsCollider.bounds;

            float halfHeight = MainCamera.orthographicSize;
            float halfWidth  = MainCamera.orthographicSize * MainCamera.aspect;

            float minX = bounds.min.x + halfWidth;
            float maxX = bounds.max.x - halfWidth;
            float minY = bounds.min.y + halfHeight;
            float maxY = bounds.max.y - halfHeight;

            // 缩放过大导致视口超出 bounds 时，居中
            if (minX > maxX) { float cx = (minX + maxX) * 0.5f; minX = maxX = cx; }
            if (minY > maxY) { float cy = (minY + maxY) * 0.5f; minY = maxY = cy; }

            camPos.x = Mathf.Clamp(camPos.x, minX, maxX);
            camPos.y = Mathf.Clamp(camPos.y, minY, maxY);
            return camPos;
        }

        // ───────────────────────── 公开 API ─────────────────────────

        /// <summary>
        /// 设置摄像头移动边界的 Collider2D 引用（由 IslandEncounterController 在岛屿停靠后调用）。
        /// 存储 Collider2D 引用而非 Bounds 值，每次 Clamp 时实时读取 bounds，确保跟随岛屿位置。
        /// </summary>
        public void SetCameraBoundsCollider(Collider2D collider)
        {
            _cameraBoundsCollider = collider;
            if (collider != null)
                Debug.Log($"[Camera] 设置当前岛屿 CameraBounds = center:{collider.bounds.center}, size:{collider.bounds.size}");
            else
                Debug.Log("[Camera] 设置 CameraBounds = null");
        }

        /// <summary>
        /// 清除摄像头移动边界的 Collider2D 引用。
        /// </summary>
        public void ClearCameraBoundsCollider()
        {
            _cameraBoundsCollider = null;
            Debug.Log("[Camera] 清除 CameraBounds");
        }

        /// <summary>
        /// 启用自由摄像头：WASD/方向键/鼠标拖拽平移 + 滚轮缩放。
        /// 仅在镜头到达 IslandCameraPoint 后调用。
        /// </summary>
        public void EnableFreeCamera()
        {
            _freeCameraEnabled = true;
            _isMoving = false;
            _targetPoint = null;
            _enableFreeCameraOnArrival = false;
            _returningToShip = false;
            _isDragging = false;
            Debug.Log("[Camera] 自由镜头开启");
        }

        /// <summary>
        /// 关闭自由摄像头。镜头保持在当前位置。
        /// 关闭后 WASD/方向键/拖拽/滚轮全部无效。
        /// </summary>
        public void DisableFreeCamera()
        {
            _freeCameraEnabled = false;
            _enableFreeCameraOnArrival = false;
            _isDragging = false;
            Debug.Log("[Camera] 自由镜头关闭");
        }

        /// <summary>
        /// 平滑移动到指定构图点（自动镜头，独占 Camera）。
        /// 调用后自由摄像头立即关闭，任何玩家输入（WASD/拖拽/滚轮）都不能改 Camera transform。
        /// </summary>
        /// <param name="cameraPoint">目标构图点</param>
        /// <param name="enableFreeCameraOnArrival">到达后是否自动启用自由摄像头</param>
        public void MoveTo(Transform cameraPoint, bool enableFreeCameraOnArrival = false)
        {
            if (cameraPoint == null)
            {
                Debug.LogWarning("[CameraDirector] MoveTo 传入了 null");
                return;
            }

            // 开始自动移动时，必须关闭自由镜头
            _freeCameraEnabled = false;
            _enableFreeCameraOnArrival = enableFreeCameraOnArrival;
            _targetPoint = cameraPoint;
            _isMoving = true;
            _returningToShip = false;
            _isDragging = false;

            Debug.Log($"[CameraDirector] 开始自动镜头移动 → {cameraPoint.name}"
                + $"{(enableFreeCameraOnArrival ? "（到达后启用自由镜头）" : "")}");
        }

        // ───────────────────────── 战斗结束回调 ─────────────────────────

        /// <summary>
        /// 战斗结束时：关闭自由摄像头 + 清除边界 + 镜头从当前位置平滑回到 ShipCameraPoint。
        /// 回船镜头期间 WASD/拖拽/滚轮全部无效。
        /// 同时把 Camera.orthographicSize 平滑恢复到 DefaultZoom。
        /// </summary>
        private void OnBattleEnded(bool alliesWon)
        {
            // 1. 立刻关闭自由摄像头（无论胜负）
            _enableFreeCameraOnArrival = false;
            DisableFreeCamera();

            // 2. 清除边界引用
            ClearCameraBoundsCollider();

            if (!alliesWon) return;

            // 3. 胜利：从当前位置平滑回到 ShipCameraPoint
            if (ShipCameraPoint != null)
            {
                // 先记录缩放参数（MoveTo 会重置 _returningToShip，所以要在 MoveTo 之后再设）
                float zoomStart = MainCamera.orthographicSize;
                float zoomTarget = DefaultZoom;

                // 预估回船位置移动总时长（距离/速度），让缩放同步完成
                float distToShip = Vector2.Distance(
                    new Vector2(MainCamera.transform.position.x, MainCamera.transform.position.y),
                    new Vector2(ShipCameraPoint.position.x, ShipCameraPoint.position.y));
                float zoomDuration = MoveSpeed > 0f ? distToShip / MoveSpeed : 1f;

                MoveTo(ShipCameraPoint);

                // MoveTo 会把 _returningToShip 重置为 false，所以必须在这里重新设为 true
                _returningToShip = true;
                _zoomStart = zoomStart;
                _zoomTarget = zoomTarget;
                _zoomElapsed = 0f;
                _zoomDuration = zoomDuration;
                Debug.Log($"[Camera] 开始自动回船镜头（缩放 {_zoomStart:F1} → {_zoomTarget:F1}，预计 {_zoomDuration:F1}s）");
            }
        }

        // ───────────────────────── 内部工具 ─────────────────────────

        /// <summary>立刻对准某个构图点（无动画）</summary>
        private void SnapTo(Transform point)
        {
            Vector3 pos = MainCamera.transform.position;
            pos.x = point.position.x;
            pos.y = point.position.y;
            pos.z = _originalZ;
            MainCamera.transform.position = pos;
        }
    }
}
