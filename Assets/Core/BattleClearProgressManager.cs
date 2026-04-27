using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace TideboundWar
{
    /// <summary>
    /// 清剿进度管理器。
    /// 战斗棋盘阶段，消除 MonsterTide 方块时增加清剿进度。
    /// 每次进入战斗棋盘时自动重置进度。
    /// 进度达到波次节点时自动刷怪。
    ///
    /// 挂载位置：GameManager 或 Systems 空物体上。
    /// </summary>
    public class BattleClearProgressManager : MonoBehaviour
    {
        [Header("清剿进度参数")]
        [Tooltip("清剿进度上限")] public int MaxClearProgress = 100;
        [Tooltip("当前清剿进度（只读/可调试）")] public int CurrentClearProgress = 0;
        [Tooltip("每消除 1 个 MonsterTide 方块增加多少进度")] public int ProgressPerMonsterTide = 1;

        [Header("引用")]
        [Tooltip("阶段管理器（判断当前是否处于战斗阶段）")]
        public PhaseManager PhaseMgr;
        [Tooltip("战斗管理器（波次刷出的敌人需要加入战斗）")]
        public BattleManager BattleManager;

        [Header("清剿进度 UI")]
        [Tooltip("清剿进度文本（TextMeshProUGUI），显示格式：清剿：0 / 100")]
        public TextMeshProUGUI ClearProgressText;

        [Header("波次节点")]
        [Tooltip("波次触发阈值列表，例如 30、60、80。进度达到节点时刷一波怪")]
        public List<int> WaveThresholds = new List<int> { 30, 60, 80 };
        [Tooltip("每波刷怪数量，与 WaveThresholds 一一对应")]
        public List<int> WaveSpawnCounts = new List<int> { 1, 2, 3 };

        // ── 内部状态 ──
        private bool _isBattleBoardPhase;  // 当前是否处于战斗棋盘阶段
        private HashSet<int> _triggeredWaves;  // 已触发的波次索引，防止重复刷
        private EnemySpawner _currentEnemySpawner;  // 当前岛屿实例的敌人生成器（运行时注入，不拖 Inspector）

        /// <summary>清剿进度是否已满</summary>
        public bool IsClearComplete => CurrentClearProgress >= MaxClearProgress;

        private void OnEnable()
        {
            GameEvents.OnTileCleared += OnTileCleared;
            GameEvents.OnPhaseChanged += OnPhaseChanged;
        }

        private void OnDisable()
        {
            GameEvents.OnTileCleared -= OnTileCleared;
            GameEvents.OnPhaseChanged -= OnPhaseChanged;
        }

        private void Start()
        {
            CurrentClearProgress = 0;
            _isBattleBoardPhase = false;
            _triggeredWaves = new HashSet<int>();
            RefreshUI();
        }

        // ── 监听阶段切换，进入战斗棋盘时重置进度 ──

        private void OnPhaseChanged(GamePhase newPhase)
        {
            if (newPhase == GamePhase.Battle && !_isBattleBoardPhase)
            {
                _isBattleBoardPhase = true;
                ResetProgress();
                Debug.Log("[BattleClear] 进入战斗棋盘阶段，清剿进度已重置");
            }
            else if (newPhase != GamePhase.Battle)
            {
                _isBattleBoardPhase = false;
            }
        }

        // ── 监听方块消除，仅在战斗棋盘阶段处理 MonsterTide ──

        private void OnTileCleared(TileType type, int count, List<Vector3> worldPositions)
        {
            if (!_isBattleBoardPhase) return;
            if (type != TileType.MonsterTide) return;
            if (count <= 0) return;

            int oldProgress = CurrentClearProgress;
            CurrentClearProgress = Mathf.Min(CurrentClearProgress + count * ProgressPerMonsterTide, MaxClearProgress);
            Debug.Log($"[BattleClear] 清剿进度 +{count * ProgressPerMonsterTide}，当前 {CurrentClearProgress}/{MaxClearProgress}");
            RefreshUI();

            // ── 检查波次节点（防止一次跳跃多个节点漏刷） ──
            CheckWaveThresholds(oldProgress, CurrentClearProgress);
        }

        // ── 波次节点检查 ──

        /// <summary>
        /// 检查从 oldProgress 到 newProgress 之间跨越了哪些波次节点。
        /// 一次从 25 涨到 65 会同时触发 30 和 60 两个节点。
        /// </summary>
        private void CheckWaveThresholds(int oldProgress, int newProgress)
        {
            if (WaveThresholds == null || WaveSpawnCounts == null) return;

            int count = Mathf.Min(WaveThresholds.Count, WaveSpawnCounts.Count);
            for (int i = 0; i < count; i++)
            {
                int threshold = WaveThresholds[i];

                // 节点已被触发 或 进度未达到此节点 → 跳过
                if (_triggeredWaves.Contains(i)) continue;
                if (newProgress < threshold) continue;

                // 触发波次
                _triggeredWaves.Add(i);
                int spawnCount = WaveSpawnCounts[i];
                Debug.Log($"[BattleClear] 达到波次节点 {threshold}，刷 {spawnCount} 个敌人");
                SpawnWaveEnemies(spawnCount);
            }
        }

        // ── 波次刷怪 ──

        /// <summary>
        /// 刷一波敌人并加入 BattleManager。
        /// </summary>
        private void SpawnWaveEnemies(int count)
        {
            if (_currentEnemySpawner == null)
            {
                Debug.LogWarning("[BattleClear] 当前岛屿没有 EnemySpawner，无法刷怪");
                return;
            }

            // 让 EnemySpawner 生成敌人，返回新生成的 SimpleUnit 列表
            List<SimpleUnit> newEnemies = _currentEnemySpawner.SpawnEnemies(count);

            if (newEnemies == null || newEnemies.Count == 0)
            {
                Debug.LogWarning("[BattleClear] 波次刷怪失败：未生成任何敌人");
                return;
            }

            // 加入 BattleManager 的敌人列表
            if (BattleManager != null && BattleManager.IsBattleActive)
            {
                foreach (var enemy in newEnemies)
                {
                    if (enemy != null && !enemy.IsDead)
                        BattleManager.AddEnemy(enemy);
                }
                Debug.Log($"[BattleClear] {newEnemies.Count} 个敌人已加入 BattleManager");
            }
            else
            {
                Debug.LogWarning("[BattleClear] BattleManager 未设置或战斗未激活，新敌人不会参与战斗");
            }
        }

        // ── UI 刷新（只在数据变化时调用，不每帧刷新） ──

        private void RefreshUI()
        {
            if (ClearProgressText != null)
                ClearProgressText.text = $"清剿：{CurrentClearProgress} / {MaxClearProgress}";
        }

        // ── 外部接口 ──

        /// <summary>重置清剿进度为 0，清空已触发波次记录</summary>
        public void ResetProgress()
        {
            CurrentClearProgress = 0;
            if (_triggeredWaves != null)
                _triggeredWaves.Clear();
            RefreshUI();
        }

        /// <summary>
        /// 设置当前岛屿实例的 EnemySpawner（由 IslandEncounterController 在岛屿生成后调用）。
        /// 同时重置清剿进度和波次触发记录，准备新一轮战斗。
        /// </summary>
        public void SetCurrentEnemySpawner(EnemySpawner spawner)
        {
            _currentEnemySpawner = spawner;
            ResetProgress();
            Debug.Log($"[BattleClear] EnemySpawner 已设置：{(spawner != null ? spawner.gameObject.name : "null")}，清剿进度已重置");
        }

        /// <summary>
        /// 清空当前 EnemySpawner 引用（岛屿销毁时调用）。
        /// </summary>
        public void ClearCurrentEnemySpawner()
        {
            _currentEnemySpawner = null;
            Debug.Log("[BattleClear] EnemySpawner 引用已清空");
        }
    }
}
