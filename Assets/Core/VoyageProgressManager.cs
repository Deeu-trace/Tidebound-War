using UnityEngine;
using TMPro;

namespace TideboundWar
{
    /// <summary>
    /// 航行进度管理器。
    /// 玩家每次有效三消结算后增加进度，进度满后触发岛屿遭遇。
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
        [Tooltip("岛屿遭遇控制器（航行满后直接触发遭遇）")]
        public IslandEncounterController IslandEncounterCtrl;
        [Tooltip("阶段管理器（进度满后切换阶段锁定三消盘）")]
        public PhaseManager PhaseMgr;

        [Header("航行进度 UI")]
        [Tooltip("航行进度文本（TextMeshProUGUI），显示格式：航行：0 / 10")]
        public TextMeshProUGUI ProgressText;

        [Header("锁定")]
        [Tooltip("航行完成后是否锁定三消盘")] public bool LockMatchBoardWhenComplete = true;

        // ── 内部状态 ──
        private bool _voyageComplete;  // 只触发一次
        private bool _encounterStarting;

        /// <summary>航行是否已完成</summary>
        public bool IsVoyageComplete => _voyageComplete;

        private void OnEnable()
        {
            GameEvents.OnMatchResolved += OnMatchResolved;
        }

        private void OnDisable()
        {
            GameEvents.OnMatchResolved -= OnMatchResolved;
        }

        private void Start()
        {
            CurrentProgress = 0;
            _voyageComplete = false;
            RefreshUI();
        }

        // ── 每次实际消除后增加进度（连锁也会多次触发） ──
        private void OnMatchResolved(TileType type, int count)
        {
            if (_voyageComplete || _encounterStarting)
            {
                Debug.Log("[Voyage] 航行已完成，锁定后续连锁进度");
                return;
            }
            if (count <= 0) return;
            AddProgressByClearedPieces(count);
        }

        /// <summary>
        /// 按消除方块数量增加航行进度：progressAdd = clearedCount * ProgressPerClearedPiece。
        /// </summary>
        public void AddProgressByClearedPieces(int clearedCount)
        {
            if (clearedCount <= 0) return;
            float amount = clearedCount * ProgressPerClearedPiece;
            AddProgress(amount);
        }

        /// <summary>
        /// 增加航行进度。达到上限后触发 OnVoyageComplete（只触发一次）。
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
                OnVoyageComplete();
            }
        }

        /// <summary>
        /// 航行完成：触发岛屿遭遇 + 锁定三消盘。
        /// </summary>
        private void OnVoyageComplete()
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

            // 2. 单岛模式：直接触发岛屿遭遇（绕开 EncounterSequenceManager）
            if (IslandEncounterCtrl != null)
            {
                IslandEncounterCtrl.BeginEncounter();
            }
            else
            {
                Debug.LogError("[Voyage] 单岛模式触发失败：请在 Inspector 设置 IslandEncounterCtrl");
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
