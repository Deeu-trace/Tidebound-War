using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace TideboundWar
{
    /// <summary>
    /// 航行进度管理器。
    /// 玩家每次有效三消结算后增加进度，进度满后等动画结束再触发岛屿遭遇。
    /// 
    /// 挂载位置：GameManager 或 Systems 空物体上。
    /// </summary>
    public class VoyageProgressManager : MonoBehaviour
    {
        [Header("进度参数")]
        [Tooltip("航行进度上限")] public float MaxProgress = 10f;
        [Tooltip("当前航行进度（只读/可调试）")] public float CurrentProgress = 0f;
        [Tooltip("每消除 1 个方块增加多少进度（可小数）")] public float ProgressPerClearedPiece = 1f;

        [Header("引用")]
        [Tooltip("遭遇序列管理器（航行满后通过它获取下一个岛屿）")]
        public EncounterSequenceManager EncounterSequenceMgr;
        [Tooltip("岛屿遭遇控制器（单岛模式回退用，多岛模式优先用 EncounterSequenceMgr）")]
        public IslandEncounterController IslandEncounterCtrl;
        [Tooltip("阶段管理器（进度满后切换阶段锁定三消盘）")]
        public PhaseManager PhaseMgr;

        [Header("航行进度 UI")]
        [Tooltip("航行进度文本（TextMeshProUGUI），显示格式：航行：0 / 10")]
        public TextMeshProUGUI ProgressText;

        [Header("锁定")]
        [Tooltip("航行完成后是否锁定三消盘")] public bool LockMatchBoardWhenComplete = true;

        // ── 内部状态 ──
        private bool _voyageComplete;           // 航行进度已满（用于去重）
        private bool _encounterStarting;        // 岛屿遭遇已开始（防重入）
        private bool _pendingVoyageComplete;    // 进度满但动画还没结束，等 OnBoardResolveComplete 再触发

        /// <summary>航行是否已完成</summary>
        public bool IsVoyageComplete => _voyageComplete;

        private void OnEnable()
        {
            GameEvents.OnTileCleared += OnTileCleared;
            GameEvents.OnBoardResolveComplete += OnBoardResolveComplete;
        }

        private void OnDisable()
        {
            GameEvents.OnTileCleared -= OnTileCleared;
            GameEvents.OnBoardResolveComplete -= OnBoardResolveComplete;
        }

        private void Start()
        {
            CurrentProgress = 0;
            _voyageComplete = false;
            _encounterStarting = false;
            _pendingVoyageComplete = false;
            RefreshUI();
        }

        // ── 每次方块消除时增加进度（连锁也会多次触发） ──
        private void OnTileCleared(TileType type, int count, List<Vector3> worldPositions)
        {
            if (_voyageComplete || _encounterStarting) return;
            if (count <= 0) return;
            AddProgressByClearedPieces(count);
        }

        // ── 动画全部结束后，检查是否需要触发岛屿 ──
        private void OnBoardResolveComplete()
        {
            if (_pendingVoyageComplete)
            {
                _pendingVoyageComplete = false;
                TriggerVoyageComplete();
            }
        }

        /// <summary>
        /// 按消除方块数量增加航行进度：progressAdd = clearedCount * ProgressPerClearedPiece。
        /// 进度满时不立刻触发岛屿，等 OnBoardResolveComplete。
        /// </summary>
        public void AddProgressByClearedPieces(int clearedCount)
        {
            if (clearedCount <= 0) return;
            float amount = clearedCount * ProgressPerClearedPiece;
            AddProgress(amount);
        }

        /// <summary>
        /// 增加航行进度。达到上限后标记 _pendingVoyageComplete，
        /// 等棋盘动画结束后由 OnBoardResolveComplete 触发岛屿。
        /// </summary>
        public void AddProgress(float amount)
        {
            if (_voyageComplete) return;
            if (amount <= 0f) return;

            CurrentProgress = Mathf.Min(CurrentProgress + amount, MaxProgress);
            Debug.Log($"[Voyage] 航行进度 +{amount:F2}，当前 {CurrentProgress:F2}/{MaxProgress:F2}");
            RefreshUI();

            if (CurrentProgress >= MaxProgress && !_voyageComplete)
            {
                // 不立刻触发岛屿，等动画结束
                _pendingVoyageComplete = true;
                Debug.Log("[Voyage] 航行进度已满，等待动画结束后触发岛屿遭遇");
            }
        }

        /// <summary>
        /// 实际触发航行完成：锁定三消盘 + 触发岛屿遭遇。
        /// </summary>
        private void TriggerVoyageComplete()
        {
            if (_encounterStarting) return;
            _voyageComplete = true;
            _encounterStarting = true;
            Debug.Log("[Voyage] 航行进度已满，触发岛屿遭遇");

            // 1. 锁定三消盘
            if (LockMatchBoardWhenComplete)
            {
                if (PhaseMgr != null)
                {
                    PhaseMgr.SetPhase(GamePhase.TransitionOut);
                    Debug.Log("[Voyage] 三消盘已锁定（阶段切换为 TransitionOut）");
                }
                else
                {
                    Debug.LogWarning("[Voyage] PhaseManager 未设置，无法锁定三消盘");
                }
            }

            // 2. 触发岛屿遭遇：优先走序列模式，回退到单岛模式
            if (EncounterSequenceMgr != null)
            {
                EncounterSequenceMgr.StartNextEncounter();
            }
            else if (IslandEncounterCtrl != null)
            {
                // 单岛模式回退：直接用 Inspector 配置的 IslandPrefab
                IslandEncounterCtrl.BeginEncounter();
            }
            else
            {
                Debug.LogError("[Voyage] 触发岛屿遭遇失败：EncounterSequenceMgr 和 IslandEncounterCtrl 都未设置");
            }
        }

        // ── UI 刷新（只在数据变化时调用，不每帧刷新） ──

        private void RefreshUI()
        {
            if (ProgressText != null)
                ProgressText.text = $"航行：{Mathf.FloorToInt(CurrentProgress)} / {Mathf.FloorToInt(MaxProgress)}";
        }

        // ── 外部重置（如需重新开始航行时调用） ──

        /// <summary>重置航行进度并回到可三消阶段（不触发岛屿遭遇）</summary>
        public void ResetVoyage()
        {
            CurrentProgress = 0f;
            _voyageComplete = false;
            _encounterStarting = false;
            _pendingVoyageComplete = false;
            if (PhaseMgr != null)
                PhaseMgr.SetPhase(GamePhase.Preparation);
            RefreshUI();
            Debug.Log("[Voyage] 航行进度重置");
        }

        // 兼容旧调用
        public void ResetProgress()
        {
            ResetVoyage();
        }
    }
}
