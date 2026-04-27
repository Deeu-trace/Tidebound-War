using System.Collections.Generic;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 产兵管理器：三消匹配 → 在出生点生成士兵 → 士兵自行走到站位。
    ///
    /// 流程：
    ///   1. Sword 匹配达到数量 → 入队 → 逐个产兵
    ///   2. Gold / Wood / Stone 匹配 → 调用 ResourceManager 增加资源
    ///   3. 生成时：在 AllySpawnOrigin 出生 → 预分配最终站位 → 调用 BeginEntering()
    ///   4. SimpleUnit 自己走过去，到位后自动 IdleReady → Wandering
    ///
    /// 站位分配防撞：
    ///   - 采样时同时检查已存活单位和已预分配（但还在路上）的站位
    ///   - 用 _reservedPositions 列表记录已分配但尚未到位的站位
    /// </summary>
    public class UnitSpawner : MonoBehaviour
    {
        [Header("预制体")]
        [Tooltip("士兵预制体")] public GameObject SoldierPrefab;

        [Header("出生点")]
        [Tooltip("所有新兵的统一出生原点（场景中的空对象）")]
        public Transform AllySpawnOrigin;

        [Header("组织")]
        [Tooltip("生成的友军父容器（场景中的空对象，用于层级整洁）")]
        public Transform AllyContainer;

        [Header("产兵区域")]
        [Tooltip("产兵区域（PolygonCollider2D，勾 Trigger）")]
        public PolygonCollider2D SpawnArea;

        [Header("站位参数")]
        [Tooltip("友军之间最小距离")] public float MinSpacing = 1.0f;
        [Tooltip("随机采样最大尝试次数")] public int MaxSampleAttempts = 50;
        [Tooltip("Sprite Pivot 偏移补偿")]
        public float PivotOffsetY = 0f;

        [Header("产兵规则")]
        [Tooltip("每几个同类型方块出 1 个兵")] public int MatchesPerUnit = 3;
        [Tooltip("连续产兵间隔（秒）")] public float SpawnInterval = 0.3f;

        [Header("资源引用")]
        [Tooltip("资源管理器（Gold/Wood/Stone 消除后加资源）")]
        public ResourceManager ResourceManager;

        [Header("全局配置")]
        [Tooltip("GameConfig（读取 ResourcePerTile 等配置）")]
        public GameConfig GameConfig;

        // ── 内部状态 ──

        private readonly Dictionary<TileType, int> _matchBank = new Dictionary<TileType, int>();
        private readonly Queue<TileType> _spawnQueue = new Queue<TileType>();
        private readonly List<Transform> _aliveUnits = new List<Transform>();
        private readonly List<Vector3> _reservedPositions = new List<Vector3>();  // 已分配但单位还没到的站位
        private float _spawnTimer;  // 倒计时，≤0 时可以出下一个兵

        /// <summary>当前存活的友军列表（只读，供外部查询）</summary>
        public IReadOnlyList<Transform> AliveUnits => _aliveUnits;

        /// <summary>新兵生成事件：参数为刚生成的 SimpleUnit。LandingController 订阅此事件获取新兵。</summary>
        public event System.Action<SimpleUnit> OnUnitSpawned;


        private void Awake()
        {
            // 自动查找 AllyContainer（如果 Inspector 里没拖，按名称在场景根层级找）
            if (AllyContainer == null)
            {
                GameObject container = GameObject.Find("AllyContainer");
                if (container != null)
                {
                    AllyContainer = container.transform;
                    Debug.Log("[UnitSpawner] 自动找到 AllyContainer");
                }
                else
                {
                    Debug.LogWarning("[UnitSpawner] 场景中未找到 AllyContainer 对象，士兵将放在场景根层级");
                }
            }
        }

        private void OnEnable()
        {
            GameEvents.OnMatchResolved += OnMatchResolved;
        }

        private void OnDisable()
        {
            GameEvents.OnMatchResolved -= OnMatchResolved;
        }

        private void Update()
        {
            if (_spawnQueue.Count == 0) return;

            _spawnTimer -= Time.deltaTime;
            if (_spawnTimer > 0f) return;

            // 清理已销毁的引用 + 已到达站位的预留
            _aliveUnits.RemoveAll(t => t == null);
            CleanupReservedPositions();

            TileType type = _spawnQueue.Dequeue();
            SpawnUnit(type);

            // 如果队列还有，重置计时器；否则归零等下次入队立刻出
            _spawnTimer = _spawnQueue.Count > 0 ? SpawnInterval : 0f;
        }


        // ── 事件处理 ──

        private void OnMatchResolved(TileType type, int count)
        {
            // ── Sword：走产兵逻辑 ──
            if (type == TileType.Sword)
            {
                if (!_matchBank.ContainsKey(type))
                    _matchBank[type] = 0;
                _matchBank[type] += count;

                int unitsToSpawn = _matchBank[type] / MatchesPerUnit;
                if (unitsToSpawn <= 0) return;

                _matchBank[type] %= MatchesPerUnit;

                for (int i = 0; i < unitsToSpawn; i++)
                {
                    _spawnQueue.Enqueue(type);
                }
                return;
            }

            // ── Gold / Wood / Stone：走资源增加逻辑 ──
            if (ResourceManager == null)
            {
                Debug.LogWarning("[UnitSpawner] ResourceManager 未设置，资源方块消除后无法加资源！");
                return;
            }

            int perTile = (GameConfig != null) ? GameConfig.ResourcePerTile : 1;
            int amount = count * perTile;

            switch (type)
            {
                case TileType.Gold:
                    ResourceManager.AddGold(amount);
                    break;
                case TileType.Wood:
                    ResourceManager.AddWood(amount);
                    break;
                case TileType.Stone:
                    ResourceManager.AddStone(amount);
                    break;
            }
        }

        // ── 生成逻辑 ──

        private void SpawnUnit(TileType type)
        {
            if (SoldierPrefab == null)
            {
                Debug.LogError("[UnitSpawner] SoldierPrefab 未设置！");
                return;
            }

            if (SpawnArea == null)
            {
                Debug.LogError("[UnitSpawner] SpawnArea 未设置！");
                return;
            }

            // ── 1. 预分配最终站位 ──
            Vector3 anchorPos = SampleValidPosition();
            anchorPos.y -= PivotOffsetY;

            // 校正：确保站位在区域内
            anchorPos = ForcePositionInsidePolygon(anchorPos);

            // 记录到预留列表（防止后续分配抢同一个点）
            _reservedPositions.Add(anchorPos);

            // ── 2. 在出生原点生成 ──
            Vector3 spawnPos = AllySpawnOrigin != null ? AllySpawnOrigin.position : anchorPos;
            GameObject unitObj = Instantiate(SoldierPrefab, spawnPos, Quaternion.identity);
            unitObj.name = $"Soldier_{type}_{_aliveUnits.Count}";

            // 放到 AllyContainer 下，保持世界坐标不变
            if (AllyContainer != null)
                unitObj.transform.SetParent(AllyContainer, worldPositionStays: true);

            // 面朝右边
            Vector3 scale = unitObj.transform.localScale;
            if (scale.x < 0) scale.x = -scale.x;
            unitObj.transform.localScale = scale;

            // ── 3. 通知单位开始入场 ──
            SimpleUnit unit = unitObj.GetComponent<SimpleUnit>();
            if (unit != null)
            {
                unit.Faction = Faction.Ally;
                unit.BeginEntering(anchorPos, SpawnArea);
                // 单位到位后自动从预留列表移除
                unit.OnArrivedAtAnchor += OnUnitArrivedAtAnchor;
            }
            else
            {
                Debug.LogWarning($"[UnitSpawner] {unitObj.name} 没有 SimpleUnit 组件，无法执行入场逻辑");
            }

            // 记录到友军列表
            _aliveUnits.Add(unitObj.transform);

            // 通知外部（如 LandingController）有新兵生成
            if (unit != null)
                OnUnitSpawned?.Invoke(unit);
        }

        /// <summary>单位到达站位后，从预留列表移除</summary>
        private void OnUnitArrivedAtAnchor(SimpleUnit unit)
        {
            unit.OnArrivedAtAnchor -= OnUnitArrivedAtAnchor;
            _reservedPositions.Remove(unit.AnchorPosition);
        }

        /// <summary>
        /// 清理预留列表中已经不需要的条目
        /// （单位已销毁、或已经到位但事件没触发等安全兜底）
        /// </summary>
        private void CleanupReservedPositions()
        {
            // 如果友军列表里的单位都已经到位或不存在，对应预留可以清掉
            // 简单做法：检查每个预留位置附近是否有已存活的单位
            for (int i = _reservedPositions.Count - 1; i >= 0; i--)
            {
                bool found = false;
                Vector3 reserved = _reservedPositions[i];
                foreach (var unit in _aliveUnits)
                {
                    if (unit == null) continue;
                    if (Vector3.Distance(unit.position, reserved) < 0.5f)
                    {
                        found = true;
                        break;
                    }
                }
                // 如果没有活着的单位在这个位置附近，说明预留已失效
                if (!found)
                {
                    _reservedPositions.RemoveAt(i);
                }
            }
        }

        // ── 站位分配 ──

        /// <summary>
        /// 强制把位置拉进多边形内部。
        /// </summary>
        private Vector3 ForcePositionInsidePolygon(Vector3 pos)
        {
            Vector2 testPt = new Vector2(pos.x, pos.y);

            if (SpawnArea.OverlapPoint(testPt))
                return pos;

            Bounds bounds = SpawnArea.bounds;
            Vector2 target = new Vector2(bounds.center.x, bounds.center.y);
            Vector2 dir = (target - testPt).normalized;
            float step = 0.2f;
            int maxSteps = 50;

            for (int i = 0; i < maxSteps; i++)
            {
                testPt += dir * step;
                if (SpawnArea.OverlapPoint(testPt))
                {
                    Debug.LogWarning($"[UnitSpawner] 校正站位 ({pos.x:F2},{pos.y:F2}) → ({testPt.x:F2},{testPt.y:F2})");
                    return new Vector3(testPt.x, testPt.y, pos.z);
                }
            }

            if (SpawnArea.OverlapPoint(target))
            {
                Debug.LogError($"[UnitSpawner] 强制放置到 bounds.center");
                return new Vector3(target.x, target.y, pos.z);
            }

            Debug.LogError($"[UnitSpawner] 连 bounds.center 都不在区域内！无法校正");
            return pos;
        }

        /// <summary>
        /// 核心采样逻辑：
        ///   1. 在 collider.bounds 内随机取点 + OverlapPoint 验证
        ///   2. 检查与已有友军最小间距
        ///   3. 检查与已预分配站位的最小间距（防撞）
        /// </summary>
        private Vector3 SampleValidPosition()
        {
            Bounds bounds = SpawnArea.bounds;

            for (int attempt = 0; attempt < MaxSampleAttempts; attempt++)
            {
                float x = Random.Range(bounds.min.x, bounds.max.x);
                float y = Random.Range(bounds.min.y, bounds.max.y);
                Vector2 candidate = new Vector2(x, y);

                if (!SpawnArea.OverlapPoint(candidate))
                    continue;

                // 检查与已有友军最小间距
                if (IsTooCloseToExisting(candidate))
                    continue;

                // 检查与已预分配站位最小间距
                if (IsTooCloseToReserved(candidate))
                    continue;

                return new Vector3(candidate.x, candidate.y, 0f);
            }

            // 采样失败 → 暴力搜索
            return BruteForceSearch(bounds);
        }

        /// <summary>检查候选点与已有友军是否太近</summary>
        private bool IsTooCloseToExisting(Vector2 candidate)
        {
            foreach (var unit in _aliveUnits)
            {
                if (unit == null) continue;
                float dist = Vector2.Distance(candidate, unit.position);
                if (dist < MinSpacing)
                    return true;
            }
            return false;
        }

        /// <summary>检查候选点与已预分配站位是否太近</summary>
        private bool IsTooCloseToReserved(Vector2 candidate)
        {
            foreach (var reserved in _reservedPositions)
            {
                float dist = Vector2.Distance(candidate, reserved);
                if (dist < MinSpacing)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 暴力搜索：网格扫描找最远点
        /// </summary>
        private Vector3 BruteForceSearch(Bounds bounds)
        {
            float step = 0.5f;
            float bestDist = 0f;
            Vector3 bestPos = new Vector3(bounds.center.x, bounds.center.y, 0f);

            for (float y = bounds.min.y + step; y < bounds.max.y; y += step)
            {
                for (float x = bounds.min.x + step; x < bounds.max.x; x += step)
                {
                    Vector2 candidate = new Vector2(x, y);

                    if (!SpawnArea.OverlapPoint(candidate))
                        continue;

                    // 计算与最近友军 + 预留站位的距离
                    float nearestDist = float.MaxValue;

                    foreach (var unit in _aliveUnits)
                    {
                        if (unit == null) continue;
                        float d = Vector2.Distance(candidate, unit.position);
                        if (d < nearestDist) nearestDist = d;
                    }

                    foreach (var reserved in _reservedPositions)
                    {
                        float d = Vector2.Distance(candidate, reserved);
                        if (d < nearestDist) nearestDist = d;
                    }

                    if (nearestDist >= MinSpacing)
                        return new Vector3(candidate.x, candidate.y, 0f);

                    if (nearestDist > bestDist)
                    {
                        bestDist = nearestDist;
                        bestPos = new Vector3(candidate.x, candidate.y, 0f);
                    }
                }
            }

            Debug.LogWarning("[UnitSpawner] 暴力扫描没有找到满足间距的点，返回最远的合格点");
            return bestPos;
        }

        // ── 公开方法 ──

        public int GetBank(TileType type)
        {
            return _matchBank.TryGetValue(type, out var val) ? val : 0;
        }

        public int GetPendingCount() => _spawnQueue.Count;

        public void ResetSpawner()
        {
            _matchBank.Clear();
            _spawnQueue.Clear();
            _aliveUnits.Clear();
            _reservedPositions.Clear();
        }
    }
}
