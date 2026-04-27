using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 岛屿遭遇控制器：游戏开始时从画面外上方生成岛屿，移动到停靠点后停住。
    ///
    /// 岛屿停靠后通知 CameraDirector 切换镜头，并通知 LandingController 开始登陆。
    /// 全部士兵回船后，镜头先回到船上，然后岛屿向 IslandExitPoint 移动并销毁。
    /// </summary>
    public class IslandEncounterController : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("岛屿容器（生成的岛屿放这里）")] public Transform IslandContainer;
        [Tooltip("岛屿生成点（画面外上方）")] public Transform IslandSpawnPoint;
        [Tooltip("岛屿停靠点（船旁边的最终位置）")] public Transform IslandDockPoint;
        [Tooltip("镜头导演（停靠后通知它切换镜头 / 回船后镜头回船）")] public CameraDirector CameraDirector;
        [Tooltip("登陆控制器（停靠后通知它开始登陆）")] public LandingController LandingController;
        [Tooltip("回船控制器（订阅全部回船事件）")] public ReturnToShipController ReturnToShipCtrl;
        [Tooltip("木板动画控制器（停靠/回船时播放木板动画）")] public ShipBoardAnimatorController BoardAnimCtrl;
        [Tooltip("航行进度管理器（岛屿销毁后重置航行）")] public VoyageProgressManager VoyageProgressMgr;

        [Header("移动参数")]
        [Tooltip("停靠移动速度")] public float MoveSpeed = 2f;
        [Tooltip("到达判定距离")] public float ArrivalThreshold = 0.05f;

        [Header("离开参数")]
        [Tooltip("岛屿离开点（画面外下方）")] public Transform IslandExitPoint;
        [Tooltip("离开移动速度")] public float ExitMoveSpeed = 2f;
        [Tooltip("木板收回动画超时时间（秒），超时后强制继续流程，防止卡死")]
        public float BoardRetractTimeout = 5f;

        [Header("单岛模式")]
        [Tooltip("单岛模式下使用的岛屿预制体（多岛模式请用 EncounterSequenceManager）")]
        public GameObject IslandPrefab;

        private GameObject _currentIsland;
        private bool _isMoving;
        private bool _isExiting;
        // 注：_waitingForCameraToReturn 已移除，改用事件订阅方式判断镜头是否回到船上
        private bool _encounterActive; // 当前是否有遭遇正在进行
        private bool _leaveSequenceStarted;
        private float _boardRetractTimer;  // 木板收回超时计时器

        private void OnEnable()
        {
            if (ReturnToShipCtrl != null)
            {
                ReturnToShipCtrl.OnAllAlliesReturned += LeaveCurrentIsland;
                Debug.Log("[IslandEncounter] 已订阅 ReturnToShipController.OnAllAlliesReturned");
            }
            else
            {
                Debug.LogWarning("[IslandEncounter] ReturnToShipCtrl 未设置，无法接收全部回船事件！");
            }
        }

        private void OnDisable()
        {
            if (ReturnToShipCtrl != null)
                ReturnToShipCtrl.OnAllAlliesReturned -= LeaveCurrentIsland;
        }

        /// <summary>
        /// 岛屿遭遇完全结束后触发（岛屿已销毁）。
        /// EncounterSequenceManager 监听此事件推进序列。
        /// </summary>
        public event System.Action OnIslandEncounterFinished;

        /// <summary>
        /// 开始岛屿遭遇（由 EncounterSequenceManager 调用）。
        /// 传入具体的岛屿预制体，由本控制器负责生成和驱动。
        /// 如果当前已有岛屿，忽略重复调用。
        /// </summary>
        public void BeginEncounter(GameObject islandPrefab)
        {
            if (_encounterActive || _currentIsland != null || _isMoving || _isExiting)
            {
                Debug.LogWarning("[IslandEncounter] 当前遭遇已在进行，忽略重复 BeginEncounter");
                return;
            }

            if (islandPrefab == null)
            {
                Debug.LogError("[IslandEncounterController] 传入的 islandPrefab 为空");
                return;
            }

            Debug.Log($"[IslandEncounter] BeginEncounter: prefab = {islandPrefab.name}");
            _encounterActive = true;
            _leaveSequenceStarted = false;
            SpawnIsland(islandPrefab);
        }

        /// <summary>
        /// 单岛模式入口：使用 Inspector 配置的 IslandPrefab。
        /// 由 VoyageProgressManager 直接调用。
        /// </summary>
        public void BeginEncounter()
        {
            if (IslandPrefab == null)
            {
                Debug.LogError("[IslandEncounterController] IslandPrefab 未设置，单岛模式无法开始");
                return;
            }
            BeginEncounter(IslandPrefab);
        }

        private void Update()
        {
            // ── 木板收回超时兜底 ──
            if (_leaveSequenceStarted && !_isExiting && _boardRetractTimer > 0f)
            {
                _boardRetractTimer -= Time.deltaTime;
                if (_boardRetractTimer <= 0f)
                {
                    Debug.LogWarning("[IslandEncounter] 木板收回动画超时，强制继续流程");
                    OnBoardRetracted();
                }
            }

            // ── 停靠移动 ──
            if (_isMoving && _currentIsland != null)
            {
                Vector3 target = IslandDockPoint.position;
                Vector3 current = _currentIsland.transform.position;

                Vector3 toTarget = target - current;
                toTarget.z = 0f;

                float dist = toTarget.magnitude;
                if (dist <= ArrivalThreshold)
                {
                    // 到达停靠点，停住
                    _currentIsland.transform.position = target;
                    _isMoving = false;
                    Debug.Log("[IslandEncounterController] 岛屿已停靠");

                    // 通知镜头导演切换到岛屿镜头点
                    NotifyCameraDirector();

                    // 先播放木板伸出动画，等完成后再通知登陆
                    if (BoardAnimCtrl != null)
                    {
                        Debug.Log("[IslandEncounterController] 等待木板伸出完成...");
                        BoardAnimCtrl.PlayExtend(OnBoardExtended);
                    }
                    else
                    {
                        // 没有木板动画，直接登陆
                        NotifyLandingController();
                    }
                }
                else
                {
                    // 移动
                    Vector3 dir = toTarget.normalized;
                    _currentIsland.transform.position += dir * MoveSpeed * Time.deltaTime;
                }
            }

            // ── 离开移动 ──
            if (_isExiting && _currentIsland != null)
            {
                if (IslandExitPoint == null)
                {
                    Debug.LogWarning("[IslandEncounterController] IslandExitPoint 未设置，无法离开");
                    _isExiting = false;
                    return;
                }

                Vector3 target = IslandExitPoint.position;
                Vector3 current = _currentIsland.transform.position;

                Vector3 toTarget = target - current;
                toTarget.z = 0f;

                float dist = toTarget.magnitude;
                if (dist <= ArrivalThreshold)
                {
                    string islandName = _currentIsland != null ? _currentIsland.name : "岛屿";
                    Destroy(_currentIsland);
                    _currentIsland = null;
                    _isExiting = false;
                    _encounterActive = false;
                    _leaveSequenceStarted = false;

                    Debug.Log($"[IslandEncounter] 当前岛屿已销毁：{islandName}");

                    // 双保险：如果 EncounterSequenceManager 没有订阅或不存在，
                    // 由 IslandEncounterController 自己重置航行进度
                    if (VoyageProgressMgr != null)
                    {
                        VoyageProgressMgr.ResetVoyage();
                        Debug.Log("[EncounterSequence] 遭遇结束，进入下一次航行");
                    }

                    OnIslandEncounterFinished?.Invoke();
                    return;
                }

                // 移向离开点
                Vector3 dir = toTarget.normalized;
                _currentIsland.transform.position += dir * ExitMoveSpeed * Time.deltaTime;
            }
        }

        private void SpawnIsland(GameObject islandPrefab)
        {
            if (IslandSpawnPoint == null)
            {
                Debug.LogError("[IslandEncounterController] IslandSpawnPoint 未设置！");
                return;
            }

            // 在生成点位置实例化
            _currentIsland = Instantiate(islandPrefab, IslandSpawnPoint.position, Quaternion.identity);

            // 放到容器下
            if (IslandContainer != null)
                _currentIsland.transform.SetParent(IslandContainer);

            _currentIsland.name = islandPrefab.name;
            Debug.Log($"[IslandEncounter] 当前岛屿实例 = {_currentIsland.name}");
            Debug.Log($"[IslandEncounter] 当前岛屿 parent = {(_currentIsland.transform.parent != null ? _currentIsland.transform.parent.name : "null")}");

            // 开始移动
            _isMoving = true;
            Debug.Log($"[IslandEncounterController] {islandPrefab.name} 已生成，开始移动");
        }

        /// <summary>
        /// 从当前岛屿实例中查找 IslandCameraPoint，通知 CameraDirector。
        /// 不用 GameObject.Find，按层级路径从已知实例查找。
        /// </summary>
        private void NotifyCameraDirector()
        {
            if (CameraDirector == null)
            {
                Debug.LogWarning("[IslandEncounterController] CameraDirector 未设置，跳过镜头切换");
                return;
            }

            if (_currentIsland == null) return;

            // 按岛屿预制体结构查找：Island01/Points/IslandCameraPoint
            Transform islandCamPoint = _currentIsland.transform.Find("Points/IslandCameraPoint");

            if (islandCamPoint != null)
            {
                CameraDirector.MoveTo(islandCamPoint);
            }
            else
            {
                Debug.LogWarning("[IslandEncounterController] 岛屿实例中未找到 Points/IslandCameraPoint");
            }
        }

        /// <summary>
        /// 通知 LandingController 开始登陆，传入当前岛屿实例。
        /// </summary>
        private void NotifyLandingController()
        {
            if (LandingController == null)
            {
                Debug.LogWarning("[IslandEncounterController] LandingController 未设置，跳过登陆");
                return;
            }

            if (_currentIsland == null) return;

            LandingController.BeginLanding(_currentIsland);
        }

        // ── 岛屿离开 ──

        /// <summary>
        /// 全部士兵回船后调用。
        /// 先收木板，收回完成后镜头回船，最后岛屿离开。
        /// </summary>
        public void LeaveCurrentIsland()
        {
            if (_leaveSequenceStarted) return;

            if (_currentIsland == null)
            {
                Debug.LogWarning("[IslandEncounterController] LeaveCurrentIsland：当前没有岛屿实例，跳过");
                return;
            }

            if (IslandExitPoint == null)
            {
                Debug.LogWarning("[IslandEncounterController] IslandExitPoint 未设置，岛屿无法离开");
                return;
            }

            _isMoving = false; // 停止停靠移动（理论上已经停靠完成）
            _leaveSequenceStarted = true;
            Debug.Log("[IslandEncounter] 收到全部回船事件，准备收回木板");

            // ── 先收木板 ──
            RetractBoardThenReturnCamera();
        }

        /// <summary>木板伸出完成回调：开始登陆</summary>
        private void OnBoardExtended()
        {
            Debug.Log("[IslandEncounterController] 木板伸出完成，开始登陆");
            NotifyLandingController();
        }

        /// <summary>木板收回完成回调：让岛屿离开</summary>
        private void OnBoardRetracted()
        {
            _boardRetractTimer = 0f; // 重置超时
            Debug.Log("[IslandEncounter] 木板收回完成，岛屿开始离开");
            ReturnCameraAndExit();
        }

        /// <summary>
        /// 镜头回到船上后的回调。播放木板收回动画。
        /// </summary>
        private void OnCameraReturnedToShip()
        {
            if (CameraDirector != null)
                CameraDirector.OnCameraArrived -= OnCameraReturnedToShip;

            Debug.Log("[IslandEncounter] 镜头已回到船上，岛屿开始离开");
            StartIslandExit();
        }

        /// <summary>
        /// 播放木板收回动画，完成后回船镜头。
        /// </summary>
        private void RetractBoardThenReturnCamera()
        {
            if (BoardAnimCtrl != null)
            {
                Debug.Log("[IslandEncounter] 开始收回木板");
                _boardRetractTimer = BoardRetractTimeout;
                BoardAnimCtrl.PlayRetract(OnBoardRetracted);
            }
            else
            {
                Debug.LogWarning("[IslandEncounter] BoardAnimCtrl 未设置，跳过木板收回，直接回镜头");
                ReturnCameraAndExit();
            }
        }

        private void ReturnCameraAndExit()
        {
            if (CameraDirector != null && CameraDirector.ShipCameraPoint != null)
            {
                CameraDirector.OnCameraArrived -= OnCameraReturnedToShip;
                CameraDirector.OnCameraArrived += OnCameraReturnedToShip;
                CameraDirector.MoveTo(CameraDirector.ShipCameraPoint);
            }
            else
            {
                StartIslandExit();
            }
        }

        /// <summary>
        /// 实际开始岛屿离开移动。
        /// </summary>
        private void StartIslandExit()
        {
            _isExiting = true;
            Debug.Log("[IslandEncounter] 岛屿开始离开，移向 IslandExitPoint");
        }
    }
}
