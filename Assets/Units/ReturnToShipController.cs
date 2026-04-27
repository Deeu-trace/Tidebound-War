using System;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

namespace TideboundWar
{
    /// <summary>
    /// 回船控制器：战斗胜利后，让幸存的我方士兵按顺序回到 Ship1 上。
    ///
    /// 边撤退边上船：
    ///   - 所有士兵同时（短间隔）从战斗位置撤到 LandingPoint 周围的等待点
    ///   - 每个士兵到达等待点后，立刻加入上船队列
    ///   - 不需要等所有人到齐，先到先上船
    ///   - 上船路线：等待点 → LandingPoint → BoardingPoint → ShipGatherArea 随机点
    ///
    /// 关键设计：
    ///   - 使用 SimpleUnit.BeginMoveAlongPath + 直接回调，不依赖 OnArrivedAtAnchor 事件
    ///   - 撤退完成 → OnRetractArrived → 加入上船队列
    ///   - 上船完成 → OnBoardArrived → returnedCount++ → 全部完成则 FireAllReturned
    ///
    /// 士兵来源（战斗结束时全量收集）：
    ///   - BattleManager.AliveAllies：战斗中的友军
    ///   - LandingController.AllKnownAllies：正在登陆/等待登陆/已登陆的友军
    ///   - UnitSpawner.AliveUnits：所有存活友军（兜底）
    ///
    /// 分类处理：
    ///   A. 已在 ShipGatherArea 内 → 直接标记完成
    ///   B. 木板/未到 LandingPoint → 掉头回船（不走撤退路线）
    ///   C. 已在岛上 → 正常撤退+上船流程
    ///
    /// 挂载位置：GameManager 或 Systems 空物体上
    /// </summary>
    public class ReturnToShipController : MonoBehaviour
    {
        // ── 事件 ──

        /// <summary>
        /// 全部士兵回船完成后触发一次。
        /// IslandEncounterController 订阅此事件让岛屿离开。
        /// </summary>
        public event Action OnAllAlliesReturned;

        [Header("引用")]
        [Tooltip("下船入口点（Ship1/Points/BoardingPoint）")]
        public Transform BoardingPoint;
        [Tooltip("船上士兵待命区域（PolygonCollider2D，勾 Trigger）")]
        public PolygonCollider2D ShipGatherArea;
        [Tooltip("战斗管理器")]
        public BattleManager BattleManager;
        [Tooltip("登陆控制器（获取正在登陆的士兵）")]
        public LandingController LandingController;
        [Tooltip("友军产兵器（兜底获取所有存活友军）")]
        public UnitSpawner AllySpawner;
        [Tooltip("友军容器（回船后士兵放到此容器下）")]
        public Transform AllyContainer;

        [Header("移动参数")]
        [Tooltip("撤退出发间隔（秒）")] public float RetractInterval = 0.05f;
        [Tooltip("上船出发间隔（秒）")] public float BoardShipInterval = 0.2f;

        [Header("集结参数")]
        [Tooltip("船上目标点 / 等待点之间最小距离")] public float MinGatherDistance = 0.5f;
        [Tooltip("船上待命点采样最大尝试次数")] public int MaxTryCount = 50;

        [Header("撤退等待点参数")]
        [Tooltip("等待点辐射起始半径")] public float StartRadius = 0.5f;
        [Tooltip("每圈半径增量")] public float RadiusStep = 0.5f;
        [Tooltip("每圈角度采样数")] public int AnglesPerRing = 12;
        [Tooltip("最大搜索半径")] public float MaxRadius = 5f;

        [Header("调试")]
        [Tooltip("启用回船调试日志（详细状态输出）")] public bool EnableReturnDebugLog = false;
        [Tooltip("调试日志输出间隔（秒）")] public float DebugLogInterval = 1f;

        // ── 内部数据 ──

        private struct ReturnInfo
        {
            public SimpleUnit Unit;
            public Vector3[] Waypoints;
            public Vector3 ShipAnchor;
            public PolygonCollider2D ShipArea;
        }

        // ── 状态集合（防重复 + 可追踪）──

        /// <summary>本次需要回船的士兵（战斗胜利时的存活友军）</summary>
        private readonly HashSet<SimpleUnit> _returningUnits = new HashSet<SimpleUnit>();

        /// <summary>已经到达撤退等待点的士兵</summary>
        private readonly HashSet<SimpleUnit> _waitingAtShoreUnits = new HashSet<SimpleUnit>();

        /// <summary>已经加入上船队列的士兵（防重复入队）</summary>
        private readonly HashSet<SimpleUnit> _queuedUnits = new HashSet<SimpleUnit>();

        /// <summary>正在走木板回船的士兵</summary>
        private readonly HashSet<SimpleUnit> _boardingUnits = new HashSet<SimpleUnit>();

        /// <summary>已经回到船上的士兵</summary>
        private readonly HashSet<SimpleUnit> _returnedUnits = new HashSet<SimpleUnit>();

        /// <summary>已经处理完（回船/死亡/失效），不再等待的士兵</summary>
        private readonly HashSet<SimpleUnit> _processedUnits = new HashSet<SimpleUnit>();

        // ── 队列 ──

        // 撤退：士兵从战斗位置走到等待点
        private readonly Queue<ReturnInfo> _retractQueue = new Queue<ReturnInfo>();
        private float _retractTimer;

        // 上船：士兵从等待点走到船上
        private readonly Queue<ReturnInfo> _boardQueue = new Queue<ReturnInfo>();
        private float _boardTimer;
        private bool _isProcessingBoardQueue;

        // ── 计数 ──
        private int _returnCount;
        private int _returnedCount;
        private bool _allReturnedFired;
        private bool _isActive;

        // ── 缓存 ──
        private readonly List<Vector3> _assignedShipPoints = new List<Vector3>();
        private readonly Dictionary<SimpleUnit, Vector3> _shoreWaitPoints = new Dictionary<SimpleUnit, Vector3>();
        private readonly List<SimpleUnit> _iterationBuffer = new List<SimpleUnit>();
        private Transform _cachedLandingPoint;
        private PolygonCollider2D _cachedWalkableArea;

        // ── 调试计时 ──
        private float _debugLogTimer;

        // ── 生命周期 ──

        private void Awake()
        {
            if (AllyContainer == null)
                Debug.LogWarning("[ReturnToShip] AllyContainer 未配置，回船后单位将保持原父节点");
        }

        private void OnEnable()
        {
            if (BattleManager != null)
                BattleManager.OnBattleEnded += OnBattleEnded;
        }

        private void OnDisable()
        {
            if (BattleManager != null)
                BattleManager.OnBattleEnded -= OnBattleEnded;
        }

        private void Update()
        {
            if (!_isActive) return;

            // ── 撤退释放 ──
            UpdateRetractRelease();

            // ── 上船释放 ──
            UpdateBoardRelease();

            // ── 队列/回调可靠性恢复 ──
            RecoverQueueState();

            // ── 安全兜底：检查卡在上船中的单位 ──
            CheckStuckBoardingUnits();

            // ── 检查是否全部完成 ──
            CheckAllReturned();

            // ── 调试日志（间隔输出）──
            if (EnableReturnDebugLog)
            {
                _debugLogTimer -= Time.deltaTime;
                if (_debugLogTimer <= 0f)
                {
                    DebugReturnState();
                    _debugLogTimer = DebugLogInterval;
                }
            }
        }

        // ── 战斗结束回调 ──

        private void OnBattleEnded(bool alliesWon)
        {
            if (!alliesWon) return;
            BeginReturn();
        }

        // ── 主入口 ──

        public void BeginReturn()
        {
            if (BoardingPoint == null) { Debug.LogError("[ReturnToShip] BoardingPoint 未设置！"); return; }
            if (ShipGatherArea == null) { Debug.LogError("[ReturnToShip] ShipGatherArea 未设置！"); return; }

            // ── 1. 全量收集所有存活友军 ──
            var allAllies = new HashSet<SimpleUnit>();

            // 来源 A: BattleManager 中的战斗友军
            if (BattleManager != null)
            {
                foreach (var unit in BattleManager.AliveAllies)
                {
                    if (unit == null) continue;
                    if (unit.IsDead || unit.State == UnitState.Dead) continue;
                    if (unit.Faction != Faction.Ally) continue;
                    allAllies.Add(unit);
                }
            }

            // 来源 B: LandingController 中的登陆中/等待登陆/已登陆的友军
            if (LandingController != null)
            {
                foreach (var unit in LandingController.AllKnownAllies)
                {
                    if (unit == null) continue;
                    if (unit.IsDead || unit.State == UnitState.Dead) continue;
                    allAllies.Add(unit);
                }

                // 取消等待登陆队列：还没出发的留在船上
                LandingController.CancelPendingLandings();
            }

            // 来源 C: UnitSpawner 兜底（可能有刚产出还没被任何人追踪的士兵）
            if (AllySpawner != null)
            {
                foreach (var unitTf in AllySpawner.AliveUnits)
                {
                    if (unitTf == null) continue;
                    SimpleUnit unit = unitTf.GetComponent<SimpleUnit>();
                    if (unit != null && !unit.IsDead && unit.State != UnitState.Dead && unit.Faction == Faction.Ally)
                        allAllies.Add(unit);
                }
            }

            Debug.Log($"[ReturnToShip] 接管所有我方士兵，数量 = {allAllies.Count}");

            if (allAllies.Count == 0)
            {
                Debug.LogWarning("[ReturnToShip] 没有存活友军，直接结束回船流程");
                FireAllReturned();
                return;
            }

            // ── 2. 获取岛屿路径点 ──
            Transform landingPoint = null;
            PolygonCollider2D walkableArea = null;
            if (BattleManager != null && BattleManager.IslandInstance != null)
            {
                landingPoint = BattleManager.IslandInstance.transform.Find("Points/LandingPoint");
                var waTf = BattleManager.IslandInstance.transform.Find("Areas/IslandWalkableArea");
                if (waTf != null) walkableArea = waTf.GetComponent<PolygonCollider2D>();
            }
            if (landingPoint == null)
                Debug.LogWarning("[ReturnToShip] 未找到 LandingPoint，回船路线将跳过此路点");
            if (walkableArea == null)
                Debug.LogWarning("[ReturnToShip] 未找到 IslandWalkableArea，撤退等待点将不受岛屿边界约束");

            // ── 3. 清空旧状态 ──
            _assignedShipPoints.Clear();
            _shoreWaitPoints.Clear();
            _retractQueue.Clear();
            _boardQueue.Clear();
            _returningUnits.Clear();
            _waitingAtShoreUnits.Clear();
            _queuedUnits.Clear();
            _boardingUnits.Clear();
            _returnedUnits.Clear();
            _processedUnits.Clear();

            _returnedCount = 0;
            _allReturnedFired = false;
            _isProcessingBoardQueue = false;
            _cachedLandingPoint = landingPoint;
            _cachedWalkableArea = walkableArea;
            _debugLogTimer = 0f;

            // ── 4. 分类每个士兵 ──

            var alreadyOnShip = new List<SimpleUnit>();   // 情况 A: 已在船上
            var onPlank = new List<SimpleUnit>();          // 情况 B: 木板/未到 LandingPoint
            var onIsland = new List<SimpleUnit>();         // 情况 C: 已在岛上

            foreach (var unit in allAllies)
            {
                // 情况 A: 已经在 ShipGatherArea 内
                if (unit.IsInsideArea(ShipGatherArea))
                {
                    alreadyOnShip.Add(unit);
                    continue;
                }

                // 情况 B: 还没到 LandingPoint（在船上/木板上）
                if (!unit.HasReachedLandingPoint)
                {
                    onPlank.Add(unit);
                    continue;
                }

                // 情况 C: 已经到达过 LandingPoint，或当前位置在岛上
                onIsland.Add(unit);
            }

            // ── 5. 处理情况 A: 已在船上 → 直接标记完成 ──
            foreach (var unit in alreadyOnShip)
            {
                // 不需要移动，直接标记
                _returnedUnits.Add(unit);
                _processedUnits.Add(unit);
                _returnedCount++;

                // 重置登陆状态
                unit.ResetLandingState();

                // 确保停在 IdleReady
                if (unit.State == UnitState.Landing || unit.State == UnitState.Combat)
                {
                    unit.InterruptCurrentPath();
                }

                // 放到 AllyContainer 下
                if (AllyContainer != null)
                    unit.transform.SetParent(AllyContainer, worldPositionStays: true);

                Debug.Log($"[ReturnToShip] {unit.gameObject.name} 已在船上，标记完成");
            }

            // ── 6. 处理情况 B: 木板/未到 LandingPoint → 掉头回船 ──
            foreach (var unit in onPlank)
            {
                // 从 BattleManager 释放（如果在战斗列表中）
                if (BattleManager != null)
                    BattleManager.DischargeAlly(unit);

                // 中断当前登陆路径
                unit.InterruptCurrentPath();

                // 掉头回船：当前位置 → BoardingPoint → ShipGatherArea 随机点
                Vector3 shipPoint = AssignShipPoint();
                var waypoints = new List<Vector3>();
                waypoints.Add(unit.transform.position);
                waypoints.Add(BoardingPoint.position);
                waypoints.Add(shipPoint);

                _returningUnits.Add(unit);
                _boardingUnits.Add(unit);

                unit.BeginMoveAlongPath(waypoints.ToArray(), OnBoardArrived, shipPoint, ShipGatherArea);
                _assignedShipPoints.Add(shipPoint);

                Debug.Log($"[ReturnToShip] {unit.gameObject.name} 正在木板/未到 LandingPoint，掉头回船");
            }

            // ── 7. 处理情况 C: 已在岛上 → 正常撤退+上船流程 ──
            // 生成撤退等待点
            var waitPoints = GenerateWaitPoints(onIsland.Count, landingPoint, walkableArea);

            for (int i = 0; i < onIsland.Count; i++)
            {
                SimpleUnit unit = onIsland[i];

                // 从 BattleManager 释放（如果在战斗列表中）
                if (BattleManager != null)
                    BattleManager.DischargeAlly(unit);

                // 中断可能残留的登陆路径（如果还在 Landing 状态）
                if (unit.State == UnitState.Landing)
                    unit.InterruptCurrentPath();

                Vector3 waitPos = waitPoints[i];

                // 撤退路线：当前位置 → 等待点
                var waypoints = new Vector3[]
                {
                    unit.transform.position,
                    waitPos
                };

                _returningUnits.Add(unit);

                _retractQueue.Enqueue(new ReturnInfo
                {
                    Unit = unit,
                    Waypoints = waypoints,
                    ShipAnchor = waitPos,
                    ShipArea = walkableArea
                });

                _shoreWaitPoints[unit] = waitPos;
            }

            // ── 8. 设置计数 ──
            _returnCount = alreadyOnShip.Count + onPlank.Count + onIsland.Count;

            _retractTimer = 0f;
            _boardTimer = 0f;
            _isActive = true;

            Debug.Log($"[ReturnToShip] 开始回船，共 {_returnCount} 名士兵（船上:{alreadyOnShip.Count} 掉头:{onPlank.Count} 撤退:{onIsland.Count}）");

            // 如果所有士兵都已在船上，直接完成
            if (_returnedCount >= _returnCount)
            {
                _isActive = false;
                FireAllReturned();
            }
        }

        // ── 撤退释放 ──

        private void UpdateRetractRelease()
        {
            if (_retractQueue.Count == 0) return;

            _retractTimer -= Time.deltaTime;
            if (_retractTimer > 0f) return;

            ReturnInfo info = _retractQueue.Dequeue();

            // 跳过已死亡/已处理的单位
            if (info.Unit == null || info.Unit.IsDead || info.Unit.State == UnitState.Dead || _processedUnits.Contains(info.Unit))
            {
                MarkProcessed(info.Unit);
                Debug.Log("[ReturnToShip] 撤退释放时单位已死亡/失效，跳过。");
                return;
            }

            // 使用 BeginMoveAlongPath + 直接回调，不依赖 OnArrivedAtAnchor
            info.Unit.BeginMoveAlongPath(info.Waypoints, OnRetractArrived, info.ShipAnchor, info.ShipArea);

            _retractTimer = RetractInterval;
        }

        /// <summary>
        /// 士兵到达撤退等待点 → 立即加入上船队列。
        /// 由 SimpleUnit.BeginMoveAlongPath 的路径完成回调直接调用。
        /// </summary>
        private void OnRetractArrived(SimpleUnit unit)
        {
            if (unit == null) return;

            // 跳过已死亡/已处理的
            if (unit.IsDead || unit.State == UnitState.Dead || _processedUnits.Contains(unit))
            {
                MarkProcessed(unit);
                Debug.Log($"[ReturnToShip] {unit.gameObject.name} 到达等待点时已死亡，标记处理");
                return;
            }

            _waitingAtShoreUnits.Add(unit);

            // 防止重复入队
            if (_queuedUnits.Contains(unit))
            {
                Debug.LogWarning($"[ReturnToShip] {unit.gameObject.name} 已在上船队列中，跳过重复入队");
                return;
            }

            EnqueueForBoarding(unit);
        }

        // ── 上船入队 ──

        private void EnqueueForBoarding(SimpleUnit unit)
        {
            if (unit == null || unit.IsDead || unit.State == UnitState.Dead || _processedUnits.Contains(unit) || _returnedUnits.Contains(unit))
                return;
            if (_queuedUnits.Contains(unit) || _boardingUnits.Contains(unit))
                return;

            _queuedUnits.Add(unit);

            Transform landingPoint = _cachedLandingPoint;
            Vector3 shipPoint = AssignShipPoint();

            // 上船路线：当前位置（等待点）→ LandingPoint → BoardingPoint → 船上待命点
            var waypoints = new List<Vector3>();
            waypoints.Add(unit.transform.position);

            if (landingPoint != null)
                waypoints.Add(landingPoint.position);

            waypoints.Add(BoardingPoint.position);
            waypoints.Add(shipPoint);

            _boardQueue.Enqueue(new ReturnInfo
            {
                Unit = unit,
                Waypoints = waypoints.ToArray(),
                ShipAnchor = shipPoint,
                ShipArea = ShipGatherArea
            });

            _assignedShipPoints.Add(shipPoint);

            Debug.Log($"[ReturnToShip] {unit.gameObject.name} 到达撤退等待点，加入上船队列。当前队列数量 = {_boardQueue.Count}");

            // 如果队列处理已停止，自动重启
            if (!_isProcessingBoardQueue && _boardQueue.Count > 0)
            {
                _isProcessingBoardQueue = true;
                _boardTimer = 0f;
            }
        }

        // ── 上船释放 ──

        private void UpdateBoardRelease()
        {
            if (!_isProcessingBoardQueue && _boardQueue.Count > 0)
            {
                _isProcessingBoardQueue = true;
                _boardTimer = 0f;
            }

            if (!_isProcessingBoardQueue) return;

            // 清理队列中已死亡/已处理的单位（防止卡死）
            while (_boardQueue.Count > 0)
            {
                ReturnInfo peek = _boardQueue.Peek();
                if (peek.Unit == null || peek.Unit.IsDead || peek.Unit.State == UnitState.Dead || _processedUnits.Contains(peek.Unit))
                {
                    _boardQueue.Dequeue();
                    MarkProcessed(peek.Unit);
                    Debug.Log("[ReturnToShip] 上船队列头部单位已死亡/失效，跳过。");
                }
                else
                {
                    break;
                }
            }

            if (_boardQueue.Count == 0)
            {
                // 队列空了，检查是否还有未处理的士兵
                if (_returnedCount < _returnCount && EnableReturnDebugLog)
                {
                    Debug.LogWarning("[ReturnToShip] 队列已空但仍有士兵未回船，请检查哪些士兵未完成流程。");
                    DebugReturnState();
                }
                _isProcessingBoardQueue = false;
                return;
            }

            _boardTimer -= Time.deltaTime;
            if (_boardTimer > 0f) return;

            ReturnInfo info = _boardQueue.Dequeue();

            // 再次检查（双保险）
            if (info.Unit == null || info.Unit.IsDead || info.Unit.State == UnitState.Dead || _processedUnits.Contains(info.Unit))
            {
                MarkProcessed(info.Unit);
                Debug.Log("[ReturnToShip] 放行时单位已死亡/失效，跳过。");
                return;
            }

            // 防止重复开始上船
            if (_boardingUnits.Contains(info.Unit))
            {
                Debug.LogWarning($"[ReturnToShip] {info.Unit.gameObject.name} 已在回船途中，跳过重复放行");
                return;
            }

            // 状态集合更新：从"等待"→"上船中"
            _waitingAtShoreUnits.Remove(info.Unit);
            _queuedUnits.Remove(info.Unit);
            _boardingUnits.Add(info.Unit);

            // 使用 BeginMoveAlongPath + 直接回调，不依赖 OnArrivedAtAnchor
            info.Unit.BeginMoveAlongPath(info.Waypoints, OnBoardArrived, info.ShipAnchor, info.ShipArea);

            Debug.Log($"[ReturnToShip] 放行 {info.Unit.gameObject.name} 上船，剩余队列数量 = {_boardQueue.Count}");

            _boardTimer = BoardShipInterval;
        }

        /// <summary>
        /// 士兵到达船上待命点。
        /// 由 SimpleUnit.BeginMoveAlongPath 的路径完成回调直接调用。
        /// </summary>
        private void OnBoardArrived(SimpleUnit unit)
        {
            if (unit == null) return;

            _boardingUnits.Remove(unit);

            // 跳过已死亡/已处理的
            if (unit.IsDead || unit.State == UnitState.Dead || _processedUnits.Contains(unit))
            {
                MarkProcessed(unit);
                Debug.Log($"[ReturnToShip] {unit.gameObject.name} 到达船上时已死亡，标记处理");
                return;
            }

            // 防止重复计数
            if (_returnedUnits.Contains(unit))
            {
                Debug.LogWarning($"[ReturnToShip] {unit.gameObject.name} 已计数过，跳过重复");
                return;
            }

            _returnedUnits.Add(unit);
            _returnedCount++;
            MarkProcessed(unit);

            // 重置登陆状态：下次岛屿战斗时必须重新经过 LandingPoint
            unit.ResetLandingState();

            // 将士兵放到 AllyContainer 下
            if (AllyContainer != null)
                unit.transform.SetParent(AllyContainer, worldPositionStays: true);

            Debug.Log($"[ReturnToShip] {unit.gameObject.name} 已回到船上 {_returnedCount}/{_returnCount}");

            if (_returnedCount >= _returnCount)
            {
                _isActive = false;
                FireAllReturned();
            }
        }

        // ── 处理完成标记 ──

        /// <summary>
        /// 标记士兵为已处理（死亡/失效/已回船），不再等待。
        /// 同时调整 _returnCount：如果该士兵本应回船但不会完成了，减少期望数。
        /// </summary>
        private void MarkProcessed(SimpleUnit unit)
        {
            if (unit == null) return;

            bool newlyAdded = _processedUnits.Add(unit);

            // 如果这个士兵本来需要回船，但现在不会完成了（死亡/失效），减少期望数
            // 已回船的不减（returnCount 应等于 returnedCount + 仍需等待的数量）
            if (newlyAdded && !_returnedUnits.Contains(unit) && _returningUnits.Contains(unit))
            {
                _returnCount = Mathf.Max(_returnedCount, _returnCount - 1);
            }

            _returningUnits.Remove(unit);
            _waitingAtShoreUnits.Remove(unit);
            _queuedUnits.Remove(unit);
            _boardingUnits.Remove(unit);
            _shoreWaitPoints.Remove(unit);
        }

        // ── 完成检查 ──

        private void CheckAllReturned()
        {
            if (_allReturnedFired) return;

            // 方式1：所有应回船的都已处理完（回船 + 死亡/失效）
            if (_processedUnits.Count >= _returnCount && _retractQueue.Count == 0 && _boardQueue.Count == 0)
            {
                _isActive = false;
                FireAllReturned();
                return;
            }

            // 方式2：没有仍在追踪的单位 + 所有队列空
            if (_returningUnits.Count == 0 && _boardingUnits.Count == 0
                && _retractQueue.Count == 0 && _boardQueue.Count == 0)
            {
                _isActive = false;
                FireAllReturned();
                return;
            }

            // 队列空但仍有未完成的，仅调试模式输出
            if (EnableReturnDebugLog && _retractQueue.Count == 0 && _boardQueue.Count == 0 && _returningUnits.Count > 0)
            {
                Debug.LogWarning("[ReturnToShip] 队列已空但仍有士兵未回船，检查是否有卡住的单位");
            }
        }

        private void FireAllReturned()
        {
            if (_allReturnedFired) return;
            _allReturnedFired = true;

            Debug.Log("[ReturnToShip] 全部士兵已回船，触发 OnAllAlliesReturned");
            OnAllAlliesReturned?.Invoke();
        }

        // ── 安全兜底：检查卡在上船中的单位 ──

        /// <summary>
        /// 如果 unit 在 boardingUnits 中，但已经在 ShipGatherArea 内，
        /// 说明回调丢失，强制调 OnBoardArrived 补救。
        /// 优先还是修正确的回调（BeginMoveAlongPath），此方法仅作兜底。
        /// </summary>
        private void CheckStuckBoardingUnits()
        {
            if (ShipGatherArea == null) return;

            _iterationBuffer.Clear();
            _iterationBuffer.AddRange(_boardingUnits);

            foreach (var unit in _iterationBuffer)
            {
                if (unit == null || unit.IsDead || unit.State == UnitState.Dead)
                {
                    MarkProcessed(unit);
                    continue;
                }

                // 单位已经在 ShipGatherArea 内 → 回调丢失，强制完成
                if (ShipGatherArea.OverlapPoint(unit.transform.position))
                {
                    Debug.LogWarning($"[ReturnToShip] 兜底：{unit.gameObject.name} 已在船上但回调未触发，强制完成");
                    OnBoardArrived(unit);
                }
            }
        }

        // ── 调试日志 ──

        /// <summary>
        /// 输出回船状态汇总，用于排查士兵卡住问题。
        /// 只在 EnableReturnDebugLog = true 时按 DebugLogInterval 间隔输出。
        /// </summary>
        private void DebugReturnState()
        {
            if (!EnableReturnDebugLog) return;

            Debug.Log(
                $"[ReturnToShip Debug]\n" +
                $"  returnCount = {_returnCount}\n" +
                $"  returnedCount = {_returnedCount}\n" +
                $"  returningUnits = {_returningUnits.Count}\n" +
                $"  waitingAtShoreUnits = {_waitingAtShoreUnits.Count}\n" +
                $"  queuedUnits = {_queuedUnits.Count}\n" +
                $"  boardingUnits = {_boardingUnits.Count}\n" +
                $"  returnedUnits = {_returnedUnits.Count}\n" +
                $"  processedUnits = {_processedUnits.Count}\n" +
                $"  retractQueue.Count = {_retractQueue.Count}\n" +
                $"  boardQueue.Count = {_boardQueue.Count}\n" +
                $"  isProcessingBoardQueue = {_isProcessingBoardQueue}"
            );
        }

        /// <summary>
        /// 双保险恢复：
        /// 1) 发现已经到等待点但漏入队的士兵，自动补入队；
        /// 2) 队列里有人但处理标记停掉时，自动重启处理。
        /// </summary>
        private void RecoverQueueState()
        {
            _iterationBuffer.Clear();
            _iterationBuffer.AddRange(_returningUnits);
            foreach (var unit in _iterationBuffer)
            {
                if (unit == null || unit.IsDead || unit.State == UnitState.Dead)
                {
                    MarkProcessed(unit);
                    continue;
                }

                if (_processedUnits.Contains(unit) || _returnedUnits.Contains(unit) || _boardingUnits.Contains(unit) || _queuedUnits.Contains(unit))
                    continue;

                if (_shoreWaitPoints.TryGetValue(unit, out Vector3 waitPos))
                {
                    float threshold = Mathf.Max(0.1f, unit.ArrivalThreshold + 0.05f);
                    if (Vector2.Distance(unit.transform.position, waitPos) <= threshold)
                    {
                        _waitingAtShoreUnits.Add(unit);
                        EnqueueForBoarding(unit);
                    }
                }
            }

            if (!_isProcessingBoardQueue && _boardQueue.Count > 0)
            {
                _isProcessingBoardQueue = true;
                _boardTimer = 0f;
            }
        }

        // ── 撤退等待点生成 ──

        private List<Vector3> GenerateWaitPoints(int count, Transform landingPoint, PolygonCollider2D walkableArea)
        {
            var points = new List<Vector3>();
            var assigned = new List<Vector3>();

            Vector2 center = landingPoint != null ? (Vector2)landingPoint.position : Vector2.zero;

            for (int i = 0; i < count; i++)
            {
                Vector3? found = null;

                for (float ringRadius = StartRadius; ringRadius <= MaxRadius; ringRadius += RadiusStep)
                {
                    for (int angleIdx = 0; angleIdx < AnglesPerRing; angleIdx++)
                    {
                        float angle = (360f / AnglesPerRing) * angleIdx + Random.Range(-10f, 10f);
                        float rad = angle * Mathf.Deg2Rad;
                        float r = ringRadius + Random.Range(-RadiusStep * 0.2f, RadiusStep * 0.2f);
                        r = Mathf.Max(0.1f, r);

                        Vector2 candidate = center + new Vector2(Mathf.Cos(rad), Mathf.Sin(rad)) * r;

                        if (walkableArea != null && !walkableArea.OverlapPoint(candidate))
                            continue;

                        if (IsTooClose(candidate, assigned))
                            continue;

                        found = new Vector3(candidate.x, candidate.y, 0f);
                        break;
                    }

                    if (found != null) break;
                }

                if (found == null)
                {
                    Debug.LogWarning($"[ReturnToShip] 第 {i + 1} 个等待点未找到合法位置，使用 LandingPoint 附近");
                    Vector2 fallback = center + Random.insideUnitCircle * StartRadius;
                    found = new Vector3(fallback.x, fallback.y, 0f);
                }

                points.Add(found.Value);
                assigned.Add(found.Value);
            }

            return points;
        }

        private bool IsTooClose(Vector2 candidate, List<Vector3> assigned)
        {
            foreach (var point in assigned)
            {
                if (Vector2.Distance(candidate, point) < MinGatherDistance)
                    return true;
            }
            return false;
        }

        // ── 船上待命点分配 ──

        private Vector3 AssignShipPoint()
        {
            Bounds bounds = ShipGatherArea.bounds;

            for (int attempt = 0; attempt < MaxTryCount; attempt++)
            {
                float x = Random.Range(bounds.min.x, bounds.max.x);
                float y = Random.Range(bounds.min.y, bounds.max.y);
                Vector2 candidate = new Vector2(x, y);

                if (!ShipGatherArea.OverlapPoint(candidate))
                    continue;

                if (IsTooCloseToShipPoints(candidate))
                    continue;

                return new Vector3(candidate.x, candidate.y, 0f);
            }

            Debug.LogWarning("[ReturnToShip] 未找到满足间距的船上待命点，使用 BoardingPoint 附近");
            return BoardingPoint.position + new Vector3(
                Random.Range(-0.3f, 0.3f),
                Random.Range(-0.3f, 0.3f),
                0f);
        }

        private bool IsTooCloseToShipPoints(Vector2 candidate)
        {
            foreach (var point in _assignedShipPoints)
            {
                if (Vector2.Distance(candidate, point) < MinGatherDistance)
                    return true;
            }
            return false;
        }
    }
}
