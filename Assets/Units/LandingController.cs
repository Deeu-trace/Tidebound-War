using System.Collections.Generic;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 登陆控制器：岛屿停靠后，让船上士兵依次下船，走到岛上集结点。
    ///
    /// 三列表管理：
    ///   - _waitingToLand：等待登陆的士兵（还没出发）
    ///   - _landingUnits：正在走登陆路线的士兵（在路上）
    ///   - _landedUnits：已完成登陆、允许参战的士兵
    ///
    /// 流程：
    ///   1. IslandEncounterController 岛屿停靠后调用 BeginLanding(islandInstance)
    ///   2. 当时在船士兵按距离排序 → 入 _waitingToLand
    ///   3. 按 StartInterval 间隔依次出队，走登陆路线，移入 _landingUnits
    ///   4. 到达集结点后移入 _landedUnits
    ///   5. 集结完成条件满足后，不直接开战，进入推进阶段（Advance）
    ///   6. 推进阶段：我方士兵向 BattleTriggerArea 推进
    ///   7. 第一个士兵进入 BattleTriggerArea → 正式开战
    ///   8. 战斗开始后，UnitSpawner 新产士兵通过 OnUnitSpawned 事件入 _waitingToLand
    ///   9. 后产士兵走完登陆路线后，通过 BattleManager.AddAlly() 加入战斗
    ///
    /// 挂载位置：GameManager 或 Systems 空物体上
    /// </summary>
    public class LandingController : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("下船入口点（Ship1/Points/BoardingPoint）")]
        public Transform BoardingPoint;
        [Tooltip("友军产兵器（获取在船士兵列表）")]
        public UnitSpawner AllySpawner;
        [Tooltip("战斗管理器（开战后驱动 NvN 战斗）")]
        public BattleManager BattleManager;

        [Header("移动参数")]
        [Tooltip("士兵出发间隔（秒）")] public float StartInterval = 0.2f;

        [Header("集结参数")]
        [Tooltip("集结点之间最小距离")] public float MinGatherDistance = 0.5f;
        [Tooltip("随机集结点最大尝试次数")] public int MaxTryCount = 50;

        [Header("开战判断")]
        [Tooltip("到达比例阈值，超过此比例即触发推进")] public float BattleReadyRatio = 0.8f;
        [Tooltip("登陆阶段最大等待时间（秒），超时即触发推进")] public float MaxLandingWaitTime = 8f;
        [Tooltip("是否启用开战判断")] public bool EnableBattleCheck = true;

        // ── 内部状态 ──

        private struct LandingInfo
        {
            public SimpleUnit Unit;
            public Vector3[] Waypoints;
            public Vector3 GatherAnchor;
            public PolygonCollider2D GatherArea;
        }

        // 三列表
        private readonly List<SimpleUnit> _waitingToLand = new List<SimpleUnit>();
        private readonly List<SimpleUnit> _landingUnits = new List<SimpleUnit>();
        private readonly List<SimpleUnit> _landedUnits = new List<SimpleUnit>();

        private readonly Queue<LandingInfo> _landingQueue = new Queue<LandingInfo>();
        private readonly List<Vector3> _assignedGatherPoints = new List<Vector3>();
        private float _intervalTimer;
        private bool _isLanding;

        // 岛屿信息（整个登陆阶段有效）
        private Transform _landingPoint;
        private PolygonCollider2D _gatherArea;
        private GameObject _currentIsland;

        // 开战判断
        private float _landingStartTime;
        private bool _hasBattleStarted;
        private bool _isBattleCheckActive;

        // 推进阶段
        private bool _isAdvancePhase;
        private readonly List<SimpleUnit> _advancingUnits = new List<SimpleUnit>();
        private PolygonCollider2D _battleTriggerArea;
        private readonly List<Vector3> _assignedAdvancePoints = new List<Vector3>();

        // 防重复加入
        private readonly HashSet<SimpleUnit> _allKnownUnits = new HashSet<SimpleUnit>();

        // 战斗结束标记：阻止新兵继续下船
        private bool _battleEnded;

        // ── 生命周期 ──

        private void OnEnable()
        {
            if (AllySpawner != null)
                AllySpawner.OnUnitSpawned += OnNewUnitSpawned;
        }

        private void OnDisable()
        {
            if (AllySpawner != null)
                AllySpawner.OnUnitSpawned -= OnNewUnitSpawned;
        }

        private void Update()
        {
            // ── 登陆队列释放 ──
            if (_isLanding && _landingQueue.Count > 0)
            {
                _intervalTimer -= Time.deltaTime;
                if (_intervalTimer <= 0f)
                {
                    LandingInfo info = _landingQueue.Dequeue();
                    info.Unit.BeginLanding(info.Waypoints, info.GatherAnchor, info.GatherArea);

                    // 战斗已开始 → 走完木板后直接进战斗，不去集结点
                    if (_hasBattleStarted)
                        info.Unit.SetCombatPending();

                    // 从 waitingToLand 移到 landingUnits
                    _waitingToLand.Remove(info.Unit);
                    _landingUnits.Add(info.Unit);

                    // 订阅到达事件
                    info.Unit.OnArrivedAtAnchor += OnSoldierArrivedAtGatherPoint;

                    _intervalTimer = StartInterval;

                    if (_landingQueue.Count == 0)
                        _isLanding = false;
                }
            }

            // 如果当前没在释放但还有等待的士兵，重新启动队列
            if (!_isLanding && _landingQueue.Count > 0)
            {
                _isLanding = true;
                _intervalTimer = 0f;
            }

            // ── 开战判断（集结完成 → 进入推进阶段） ──
            if (_isBattleCheckActive && !_hasBattleStarted && EnableBattleCheck)
                CheckBattleReady();

            // ── 推进阶段：检测是否有士兵进入 BattleTriggerArea ──
            if (_isAdvancePhase && !_hasBattleStarted)
                CheckAdvanceTrigger();
        }

        // ── 新兵通知 ──

        /// <summary>
        /// UnitSpawner 新产士兵回调。
        /// 无论战斗是否已经开始，新兵都必须走登陆流程。
        /// </summary>
        private void OnNewUnitSpawned(SimpleUnit unit)
        {
            // 岛屿还没停靠过，不处理（正常不会发生，但安全起见）
            if (_currentIsland == null) return;

            // 防重复
            if (_allKnownUnits.Contains(unit)) return;
            if (unit.State == UnitState.Dead) return;

            // 战斗已结束，新兵留在船上，不安排登陆
            if (_battleEnded)
            {
                Debug.Log($"[LandingController] 战斗已结束，{unit.gameObject.name} 留在船上");
                return;
            }

            EnqueueForLanding(unit);
        }

        // ── 登陆流程 ──

        /// <summary>
        /// 岛屿停靠后由 IslandEncounterController 调用。
        /// 传入当前岛屿实例，从中获取 LandingPoint 和 AllyGatherArea。
        /// </summary>
        public void BeginLanding(GameObject islandInstance)
        {
            _currentIsland = islandInstance;

            if (BoardingPoint == null)
            {
                Debug.LogError("[LandingController] BoardingPoint 未设置！");
                return;
            }

            if (AllySpawner == null)
            {
                Debug.LogError("[LandingController] AllySpawner 未设置！");
                return;
            }

            if (islandInstance == null)
            {
                Debug.LogError("[LandingController] islandInstance 为空！");
                return;
            }

            // ── 从岛屿实例获取 LandingPoint ──
            _landingPoint = islandInstance.transform.Find("Points/LandingPoint");
            if (_landingPoint == null)
            {
                Debug.LogError("[LandingController] 岛屿实例中未找到 Points/LandingPoint！");
                return;
            }

            // ── 从岛屿实例获取 AllyGatherArea ──
            Transform gatherAreaTf = islandInstance.transform.Find("Areas/AllyGatherArea");
            _gatherArea = null;
            if (gatherAreaTf != null)
                _gatherArea = gatherAreaTf.GetComponent<PolygonCollider2D>();
            if (_gatherArea == null)
            {
                Debug.LogError("[LandingController] 岛屿实例中未找到 Areas/AllyGatherArea（PolygonCollider2D）！");
                return;
            }

            // ── 从岛屿实例获取 BattleTriggerArea ──
            Transform triggerAreaTf = islandInstance.transform.Find("Areas/BattleTriggerArea");
            _battleTriggerArea = null;
            if (triggerAreaTf != null)
                _battleTriggerArea = triggerAreaTf.GetComponent<PolygonCollider2D>();
            if (_battleTriggerArea == null)
                Debug.LogWarning("[LandingController] 岛屿实例中未找到 Areas/BattleTriggerArea（PolygonCollider2D），推进阶段将跳过");

            // ── 收集在船士兵 ──
            var soldiers = new List<SimpleUnit>();
            foreach (var unitTf in AllySpawner.AliveUnits)
            {
                if (unitTf == null) continue;
                SimpleUnit unit = unitTf.GetComponent<SimpleUnit>();
                if (unit != null && unit.State != UnitState.Dead)
                    soldiers.Add(unit);
            }

            if (soldiers.Count == 0)
            {
                Debug.LogWarning("[LandingController] 当前没有可登陆的士兵");
            }

            // ── 按距 BoardingPoint 从近到远排序 ──
            Vector2 boardingPos = BoardingPoint.position;
            soldiers.Sort((a, b) =>
                Vector2.Distance(a.transform.position, boardingPos).CompareTo(
                Vector2.Distance(b.transform.position, boardingPos)));

            // ── 清空旧状态 ──
            _assignedGatherPoints.Clear();
            _landingQueue.Clear();
            _waitingToLand.Clear();
            _landingUnits.Clear();
            _landedUnits.Clear();
            _allKnownUnits.Clear();
            _advancingUnits.Clear();
            _assignedAdvancePoints.Clear();

            // ── 分配集结点并建队 ──
            for (int i = 0; i < soldiers.Count; i++)
            {
                EnqueueForLanding(soldiers[i]);
            }

            _intervalTimer = 0f; // 第一个士兵立刻出发
            _isLanding = _landingQueue.Count > 0;

            // ── 重置开战判断状态 ──
            _landingStartTime = Time.time;
            _hasBattleStarted = false;
            _isBattleCheckActive = true;
            _isAdvancePhase = false;
            _battleEnded = false;
        }

        /// <summary>
        /// 将一个士兵加入登陆队列。
        /// 统一的入队方法：BeginLanding 和 OnNewUnitSpawned 都走这里。
        /// 
        /// 如果战斗已开始，路线缩短为 BoardingPoint → LandingPoint（不需要去集结点），
        /// 到达 LandingPoint 后直接加入 BattleManager。
        /// </summary>
        private void EnqueueForLanding(SimpleUnit unit)
        {
            if (unit == null || unit.State == UnitState.Dead) return;
            if (_allKnownUnits.Contains(unit)) return;
            if (_landedUnits.Contains(unit)) return;
            if (_landingUnits.Contains(unit)) return;

            // 分配集结点（即使战斗已开始也需要，作为 SpawnArea 使用）
            Vector3 gatherPoint = AssignGatherPoint(_gatherArea, _landingPoint);

            Vector3[] waypoints;
            if (_hasBattleStarted)
            {
                // 战斗已开始：只走到 LandingPoint，不需要去集结点
                waypoints = new Vector3[]
                {
                    BoardingPoint.position,
                    _landingPoint.position
                };
            }
            else
            {
                // 正常路线：BoardingPoint → LandingPoint → 集结点
                waypoints = new Vector3[]
                {
                    BoardingPoint.position,
                    _landingPoint.position,
                    gatherPoint
                };
            }

            _landingQueue.Enqueue(new LandingInfo
            {
                Unit = unit,
                Waypoints = waypoints,
                GatherAnchor = gatherPoint,
                GatherArea = _gatherArea
            });

            _assignedGatherPoints.Add(gatherPoint);
            _waitingToLand.Add(unit);
            _allKnownUnits.Add(unit);

            // 新兵入队时重置开战计时器（避免靠岸时0兵→8秒后新兵还没走完就超时）
            if (_isBattleCheckActive)
                _landingStartTime = Time.time;
        }

        /// <summary>士兵到达集结点回调（包括走完木板后直接进战斗的 EnterCombatAfterLanding 路径）</summary>
        private void OnSoldierArrivedAtGatherPoint(SimpleUnit unit)
        {
            unit.OnArrivedAtAnchor -= OnSoldierArrivedAtGatherPoint;

            // 防御性检查：还没到 LandingPoint 的士兵不应到达集结点
            if (!unit.HasReachedLandingPoint)
            {
                Debug.LogWarning($"[LandingController] {unit.gameObject.name} 未到 LandingPoint 却触发了到达回调，忽略");
                return;
            }

            // 从 landingUnits 移到 landedUnits
            _landingUnits.Remove(unit);
            _landedUnits.Add(unit);

            // 如果正在推进阶段（还没正式开战），让新到的士兵也加入推进
            if (_isAdvancePhase && !_hasBattleStarted)
            {
                StartAdvancingUnit(unit);
                return;
            }

            // 如果战斗已经开始，把这个已登陆的新兵加入 BattleManager
            if (_hasBattleStarted && BattleManager != null && BattleManager.IsBattleActive)
            {
                if (unit.State != UnitState.Dead)
                    BattleManager.AddAlly(unit);
            }
        }

        // ── 开战判断 ──

        /// <summary>
        /// 检查是否满足集结完成条件。满足任意一个即触发推进阶段，只触发一次。
        /// 条件1：全部到位（landedUnits + landingUnits >= totalKnown）
        /// 条件2：达到比例（landedUnits.Count / totalKnown >= BattleReadyRatio）
        /// 条件3：超时（距登陆开始已过 MaxLandingWaitTime 秒）
        /// 
        /// 集结完成后不直接开战，而是让士兵进入推进阶段（Advance），
        /// 推进到 BattleTriggerArea 后才正式开战。
        /// </summary>
        private void CheckBattleReady()
        {
            int total = _allKnownUnits.Count;
            if (total == 0) return;

            int landed = _landedUnits.Count;

            // 没有任何已登陆友军时不能推进（避免0友军推进）
            if (landed == 0) return;

            string reason = null;

            // 条件1：全部到位
            if (landed >= total)
                reason = "全部到位";

            // 条件2：达到比例
            else if ((float)landed / total >= BattleReadyRatio)
                reason = $"{BattleReadyRatio * 100:F0}%到位";

            // 条件3：超时
            else if (Time.time - _landingStartTime >= MaxLandingWaitTime)
                reason = "超时";

            if (reason != null)
            {
                _isBattleCheckActive = false;
                Debug.Log($"[LandingController] 集结完成：原因 = {reason}（已登陆 {landed}/{total}），进入推进阶段");
                BeginAdvancePhase();
            }
        }

        // ── 推进阶段 ──

        /// <summary>
        /// 让所有已集结的士兵进入推进阶段，向 BattleTriggerArea 推进。
        /// 如果没有 BattleTriggerArea，直接开战。
        /// </summary>
        private void BeginAdvancePhase()
        {
            // 没有 BattleTriggerArea → 直接开战（兼容旧岛屿）
            if (_battleTriggerArea == null)
            {
                Debug.LogWarning("[LandingController] 没有 BattleTriggerArea，跳过推进阶段直接开战");
                _hasBattleStarted = true;
                StartBattle();
                return;
            }

            _isAdvancePhase = true;
            _assignedAdvancePoints.Clear();

            // 所有已集结的士兵进入推进
            foreach (var unit in _landedUnits)
            {
                if (unit != null && unit.State != UnitState.Dead)
                    StartAdvancingUnit(unit);
            }
        }

        /// <summary>让一个士兵开始推进（分配目标点并进入 Advance 状态）</summary>
        private void StartAdvancingUnit(SimpleUnit unit)
        {
            Vector3 advanceTarget = AssignAdvancePoint(_battleTriggerArea);
            unit.BeginAdvance(advanceTarget, _battleTriggerArea);
            _advancingUnits.Add(unit);
        }

        /// <summary>
        /// 在 BattleTriggerArea 内分配一个不重叠的推进目标点。
        /// </summary>
        private Vector3 AssignAdvancePoint(PolygonCollider2D triggerArea)
        {
            Bounds bounds = triggerArea.bounds;

            for (int attempt = 0; attempt < MaxTryCount; attempt++)
            {
                float x = Random.Range(bounds.min.x, bounds.max.x);
                float y = Random.Range(bounds.min.y, bounds.max.y);
                Vector2 candidate = new Vector2(x, y);

                // 必须在区域内
                if (!triggerArea.OverlapPoint(candidate))
                    continue;

                // 与已分配推进点保持最小距离
                if (IsTooCloseToAdvancePoints(candidate))
                    continue;

                _assignedAdvancePoints.Add(new Vector3(candidate.x, candidate.y, 0f));
                return new Vector3(candidate.x, candidate.y, 0f);
            }

            // 多次尝试失败，使用区域中心附近
            Debug.LogWarning("[LandingController] 未找到满足间距的推进目标点，使用 BattleTriggerArea 中心附近");
            Vector3 fallback = new Vector3(
                bounds.center.x + Random.Range(-0.3f, 0.3f),
                bounds.center.y + Random.Range(-0.3f, 0.3f),
                0f);
            _assignedAdvancePoints.Add(fallback);
            return fallback;
        }

        private bool IsTooCloseToAdvancePoints(Vector2 candidate)
        {
            foreach (var point in _assignedAdvancePoints)
            {
                if (Vector2.Distance(candidate, point) < MinGatherDistance)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 检查推进中的士兵是否进入了 BattleTriggerArea。
        /// 第一个进入的士兵触发正式开战。
        /// </summary>
        private void CheckAdvanceTrigger()
        {
            if (_battleTriggerArea == null) return;

            foreach (var unit in _advancingUnits)
            {
                if (unit == null || unit.State == UnitState.Dead) continue;

                Vector2 pos = new Vector2(unit.transform.position.x, unit.transform.position.y);
                if (_battleTriggerArea.OverlapPoint(pos))
                {
                    Debug.Log($"[LandingController] {unit.gameObject.name} 进入 BattleTriggerArea，正式开战！");
                    _hasBattleStarted = true;
                    _isAdvancePhase = false;
                    StartBattle();
                    return;
                }
            }
        }

        /// <summary>
        /// 收集已登陆 + 推进中 + 正在登陆的士兵 + 敌军，启动 BattleManager。
        /// 
        /// 友军来源：
        ///   - _landedUnits：已到达集结点的士兵
        ///   - _advancingUnits：正在推进中的士兵（也一定已到过 LandingPoint）
        ///   - _landingUnits 中已上岛的士兵
        ///   - _landingUnits 中未上岛的士兵只设 CombatPendingAfterLanding
        /// </summary>
        private void StartBattle()
        {
            if (BattleManager == null)
            {
                Debug.LogWarning("[LandingController] BattleManager 未设置，无法开始战斗");
                return;
            }

            // ── 收集友军 ──
            var allies = new List<SimpleUnit>();

            // 1. 已到达集结点的士兵（一定已经到过 LandingPoint）
            foreach (var unit in _landedUnits)
            {
                if (unit != null && unit.State != UnitState.Dead)
                    allies.Add(unit);
            }

            // 2. 推进中的士兵（一定已经到过 LandingPoint）
            foreach (var unit in _advancingUnits)
            {
                if (unit != null && unit.State != UnitState.Dead)
                    allies.Add(unit);
            }
            _advancingUnits.Clear();

            // 3. 还在路上的士兵：按是否已到过 LandingPoint 区分处理
            foreach (var unit in _landingUnits)
            {
                if (unit == null || unit.State == UnitState.Dead) continue;

                if (unit.HasReachedLandingPoint)
                {
                    // 已上岛：取消到达回调，中断登陆路线，让 BattleManager 接管
                    unit.OnArrivedAtAnchor -= OnSoldierArrivedAtGatherPoint;
                    unit.InterruptLandingForCombat();
                    allies.Add(unit);
                }
                else
                {
                    // 还没到 LandingPoint（在船上/木板上）：
                    // 不能加入 BattleManager，只标记走完后直接进战斗
                    unit.SetCombatPending();
                    Debug.Log($"[LandingController] {unit.gameObject.name} 还没到 LandingPoint，标记走完后加入战斗");
                }
            }
            // 只清空已上岛的士兵，还在木板上的继续留在 _landingUnits
            _landingUnits.RemoveAll(u =>
                u != null && u.HasReachedLandingPoint && u.State != UnitState.Dead);

            // ── 收集敌军：从岛屿实例的 EnemyContainer 中找 ──
            var enemies = new List<SimpleUnit>();
            if (_currentIsland != null)
            {
                Transform enemyContainer = _currentIsland.transform.Find("Runtime/EnemyContainer");
                if (enemyContainer != null)
                {
                    foreach (Transform child in enemyContainer)
                    {
                        if (child == null) continue;
                        SimpleUnit unit = child.GetComponent<SimpleUnit>();
                        if (unit != null && unit.State != UnitState.Dead)
                            enemies.Add(unit);
                    }
                }
            }

            if (allies.Count == 0 || enemies.Count == 0)
            {
                Debug.LogWarning($"[LandingController] 开战条件不满足：友军 {allies.Count}，敌军 {enemies.Count}");
                return;
            }

            BattleManager.StartBattle(allies, enemies, _currentIsland);
        }

        // ── 集结点分配 ──

        /// <summary>
        /// 在 AllyGatherArea 内分配一个不重叠的集结点。
        /// 如果多次尝试找不到合法点，回退到 LandingPoint 附近并输出警告。
        /// </summary>
        private Vector3 AssignGatherPoint(PolygonCollider2D gatherArea, Transform landingPoint)
        {
            Bounds bounds = gatherArea.bounds;

            for (int attempt = 0; attempt < MaxTryCount; attempt++)
            {
                float x = Random.Range(bounds.min.x, bounds.max.x);
                float y = Random.Range(bounds.min.y, bounds.max.y);
                Vector2 candidate = new Vector2(x, y);

                // 必须在区域内
                if (!gatherArea.OverlapPoint(candidate))
                    continue;

                // 与已分配集结点保持最小距离
                if (IsTooCloseToAssigned(candidate))
                    continue;

                return new Vector3(candidate.x, candidate.y, 0f);
            }

            // 多次尝试失败，回退到 LandingPoint 附近
            Debug.LogWarning("[LandingController] 未找到满足间距的集结点，使用 LandingPoint 附近");
            if (landingPoint != null)
            {
                return landingPoint.position + new Vector3(
                    Random.Range(-0.3f, 0.3f),
                    Random.Range(-0.3f, 0.3f),
                    0f);
            }

            return new Vector3(bounds.center.x, bounds.center.y, 0f);
        }

        private bool IsTooCloseToAssigned(Vector2 candidate)
        {
            foreach (var point in _assignedGatherPoints)
            {
                if (Vector2.Distance(candidate, point) < MinGatherDistance)
                    return true;
            }
            return false;
        }

        // ── 战斗结束后对外接口 ──

        /// <summary>
        /// 所有已知的友军（包括等待登陆、正在登陆、已登陆的），供 ReturnToShipController 收集。
        /// </summary>
        public IEnumerable<SimpleUnit> AllKnownAllies
        {
            get
            {
                foreach (var unit in _waitingToLand)
                    if (unit != null && unit.State != UnitState.Dead) yield return unit;
                foreach (var unit in _landingUnits)
                    if (unit != null && unit.State != UnitState.Dead) yield return unit;
                foreach (var unit in _landedUnits)
                    if (unit != null && unit.State != UnitState.Dead) yield return unit;
            }
        }

        /// <summary>
        /// 战斗结束后由 ReturnToShipController 调用：
        /// 1. 设置 _battleEnded 标记，阻止新兵继续下船
        /// 2. 取消所有等待登陆的士兵（还没出发的留在船上）
        /// 3. 清空登陆队列
        /// 正在路上的士兵（_landingUnits）不会被取消，由 ReturnToShipController 接管。
        /// </summary>
        public void CancelPendingLandings()
        {
            _battleEnded = true;

            int cancelCount = _waitingToLand.Count;

            // 取消等待登陆的士兵的 OnArrivedAtAnchor 订阅
            foreach (var unit in _waitingToLand)
            {
                if (unit != null)
                    unit.OnArrivedAtAnchor -= OnSoldierArrivedAtGatherPoint;
            }

            _waitingToLand.Clear();
            _landingQueue.Clear();
            _isLanding = false;

            if (cancelCount > 0)
                Debug.Log($"[LandingController] 战斗已结束，取消等待登陆队列，{cancelCount} 名士兵留在船上");
        }
    }
}
