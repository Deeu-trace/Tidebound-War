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

            // ── 检查是否全部完成 ──
            CheckAllReturned();
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
            if (BattleManager == null) { Debug.LogWarning("[ReturnToShip] BattleManager 未设置"); return; }
            if (BoardingPoint == null) { Debug.LogError("[ReturnToShip] BoardingPoint 未设置！"); return; }
            if (ShipGatherArea == null) { Debug.LogError("[ReturnToShip] ShipGatherArea 未设置！"); return; }

            // ── 收集存活友军 ──
            var survivors = new List<SimpleUnit>();
            foreach (var unit in BattleManager.AliveAllies)
            {
                if (unit == null) continue;
                if (unit.State == UnitState.Dead) continue;
                survivors.Add(unit);
            }

            if (survivors.Count == 0)
            {
                Debug.LogWarning("[ReturnToShip] 没有存活友军，直接结束回船流程");
                FireAllReturned();
                return;
            }

            // ── 获取岛屿路径点 ──
            Transform landingPoint = null;
            PolygonCollider2D walkableArea = null;
            if (BattleManager.IslandInstance != null)
            {
                landingPoint = BattleManager.IslandInstance.transform.Find("Points/LandingPoint");
                var waTf = BattleManager.IslandInstance.transform.Find("Areas/IslandWalkableArea");
                if (waTf != null) walkableArea = waTf.GetComponent<PolygonCollider2D>();
            }
            if (landingPoint == null)
                Debug.LogWarning("[ReturnToShip] 未找到 LandingPoint，回船路线将跳过此路点");
            if (walkableArea == null)
                Debug.LogWarning("[ReturnToShip] 未找到 IslandWalkableArea，撤退等待点将不受岛屿边界约束");

            // ── 清空旧状态 ──
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

            _returnCount = survivors.Count;
            _returnedCount = 0;
            _allReturnedFired = false;
            _isProcessingBoardQueue = false;
            _cachedLandingPoint = landingPoint;
            _cachedWalkableArea = walkableArea;

            // ── 记录本次需要回船的士兵 ──
            foreach (var unit in survivors)
                _returningUnits.Add(unit);

            // ── 生成撤退等待点 ──
            var waitPoints = GenerateWaitPoints(survivors.Count, landingPoint, walkableArea);

            // ── 所有士兵退出战斗 + 加入撤退队列 ──
            for (int i = 0; i < survivors.Count; i++)
            {
                SimpleUnit unit = survivors[i];
                BattleManager.DischargeAlly(unit);

                Vector3 waitPos = waitPoints[i];

                // 撤退路线：当前位置 → 等待点
                var waypoints = new Vector3[]
                {
                    unit.transform.position,
                    waitPos
                };

                _retractQueue.Enqueue(new ReturnInfo
                {
                    Unit = unit,
                    Waypoints = waypoints,
                    ShipAnchor = waitPos,
                    ShipArea = walkableArea
                });

                _shoreWaitPoints[unit] = waitPos;
            }

            _retractTimer = 0f;
            _boardTimer = 0f;
            _isActive = true;

            Debug.Log($"[ReturnToShip] 开始回船，共 {survivors.Count} 名士兵");
            DebugReturnState();
        }

        // ── 撤退释放 ──

        private void UpdateRetractRelease()
        {
            if (_retractQueue.Count == 0) return;

            _retractTimer -= Time.deltaTime;
            if (_retractTimer > 0f) return;

            ReturnInfo info = _retractQueue.Dequeue();

            // 跳过已死亡/已处理的单位
            if (info.Unit == null || info.Unit.State == UnitState.Dead || _processedUnits.Contains(info.Unit))
            {
                MarkProcessed(info.Unit);
                Debug.Log("[ReturnToShip] 撤退释放时单位已死亡/失效，跳过。");
                return;
            }

            info.Unit.OnArrivedAtAnchor -= OnRetractArrived;
            info.Unit.OnArrivedAtAnchor -= OnBoardArrived;
            info.Unit.BeginLanding(info.Waypoints, info.ShipAnchor, info.ShipArea);
            info.Unit.OnArrivedAtAnchor += OnRetractArrived;

            _retractTimer = RetractInterval;
        }

        /// <summary>
        /// 士兵到达撤退等待点 → 立即加入上船队列。
        /// </summary>
        private void OnRetractArrived(SimpleUnit unit)
        {
            if (unit == null) return;
            unit.OnArrivedAtAnchor -= OnRetractArrived;

            // 跳过已死亡/已处理的
            if (unit.State == UnitState.Dead || _processedUnits.Contains(unit))
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
            if (unit == null || unit.State == UnitState.Dead || _processedUnits.Contains(unit) || _returnedUnits.Contains(unit))
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
            DebugReturnState();

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
                if (peek.Unit == null || peek.Unit.State == UnitState.Dead || _processedUnits.Contains(peek.Unit))
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
                if (_returnedCount < _returnCount)
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
            if (info.Unit == null || info.Unit.State == UnitState.Dead || _processedUnits.Contains(info.Unit))
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

            _queuedUnits.Remove(info.Unit);
            _boardingUnits.Add(info.Unit);
            info.Unit.OnArrivedAtAnchor -= OnRetractArrived;
            info.Unit.OnArrivedAtAnchor -= OnBoardArrived;
            info.Unit.BeginLanding(info.Waypoints, info.ShipAnchor, info.ShipArea);
            info.Unit.OnArrivedAtAnchor += OnBoardArrived;

            Debug.Log($"[ReturnToShip] 放行 {info.Unit.gameObject.name} 上船，剩余队列数量 = {_boardQueue.Count}");
            DebugReturnState();

            _boardTimer = BoardShipInterval;
        }

        /// <summary>
        /// 士兵到达船上待命点。
        /// </summary>
        private void OnBoardArrived(SimpleUnit unit)
        {
            if (unit == null) return;
            unit.OnArrivedAtAnchor -= OnBoardArrived;
            _boardingUnits.Remove(unit);

            // 跳过已死亡/已处理的
            if (unit.State == UnitState.Dead || _processedUnits.Contains(unit))
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
            DebugReturnState();

            if (_returnedCount >= _returnCount)
            {
                _isActive = false;
                FireAllReturned();
            }
        }

        // ── 处理完成标记 ──

        /// <summary>
        /// 标记士兵为已处理（死亡/失效/已回船），不再等待。
        /// </summary>
        private void MarkProcessed(SimpleUnit unit)
        {
            if (unit == null) return;
            _processedUnits.Add(unit);
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

            // 所有需要回船的士兵都已处理完
            bool allProcessed = true;
            _iterationBuffer.Clear();
            _iterationBuffer.AddRange(_returningUnits);
            foreach (var unit in _iterationBuffer)
            {
                if (unit == null || unit.State == UnitState.Dead)
                {
                    MarkProcessed(unit);
                    continue;
                }
                if (!_processedUnits.Contains(unit))
                {
                    allProcessed = false;
                    break;
                }
            }

            // 如果队列空但仍未完成，打印一次详细状态
            if (_retractQueue.Count == 0 && _boardQueue.Count == 0 && !allProcessed)
            {
                Debug.LogWarning("[ReturnToShip] 队列已空但仍有士兵未回船，请检查哪些士兵未完成流程。");
                DebugReturnState();
            }

            // 队列都空了 + 所有士兵都已处理
            if (allProcessed && _retractQueue.Count == 0 && _boardQueue.Count == 0)
            {
                _isActive = false;
                FireAllReturned();
            }
        }

        private void FireAllReturned()
        {
            if (_allReturnedFired) return;
            _allReturnedFired = true;

            Debug.Log("[ReturnToShip] 全部士兵已回船");
            Debug.Log($"[ReturnToShip] 全部 {_returnedCount}/{Mathf.Max(_returnCount, _returnedCount)} 士兵已回船");
            OnAllAlliesReturned?.Invoke();
        }

        // ── 调试日志 ──

        /// <summary>
        /// 输出回船状态汇总，用于排查士兵卡住问题。
        /// </summary>
        private void DebugReturnState()
        {
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
                if (unit == null || unit.State == UnitState.Dead)
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
