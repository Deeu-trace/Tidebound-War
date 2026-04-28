using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TideboundWar
{
    /// <summary>
    /// 战斗号角管理器：消除 Horn 方块累积号角进度，进度满后获得号角次数，
    /// 点击号角按钮生成援军。
    ///
    /// 挂载位置：GameManager 或 Systems 空物体上。
    ///
    /// 核心流程：
    ///   1. 战斗棋盘阶段，消除 Horn → 增加号角进度
    ///   2. 进度满后获得 1 次号角（溢出进度保留）
    ///   3. 玩家点击号角按钮 → 调用 UnitSpawner.SpawnReinforcement(prefab)
    ///   4. 援军在船上出生 → 走木板下船 → 加入战斗（完全复用现有流程）
    ///
    /// 号角使用时机：
    ///   在 BattleManager.IsBattleActive == true 或 BattleManager.IsLastChance == true 时可使用。
    ///   LastChance：我方全灭但仍有号角可用，等待玩家使用号角召唤援军。
    ///   战斗未开始、战斗结束（无论胜负）、回船、岛屿离开等阶段均不可使用。
    ///
    /// 容量满时：不生成、不消耗号角次数、输出 Warning。
    /// </summary>
    public class BattleHornManager : MonoBehaviour
    {
        // ────────────── Inspector 参数 ──────────────

        [Header("号角参数")]
        [Tooltip("号角进度上限（满后获得 1 次号角）")] public int HornMaxProgress = 10;
        [Tooltip("每消除 1 个 Horn 方块增加多少进度")] public int HornProgressPerTile = 1;
        [Tooltip("每次使用号角生成的士兵数量")] public int HornUnitsPerUse = 1;

        [Header("援军预制体")]
        [Tooltip("号角召唤的士兵预制体（留空则使用 UnitSpawner 的默认 SoldierPrefab）")]
        public GameObject HornUnitPrefab;

        [Header("引用")]
        [Tooltip("产兵管理器（生成援军）")] public UnitSpawner AllySpawner;
        [Tooltip("战斗管理器（判断战斗是否进行中）")] public BattleManager BattleMgr;
        [Tooltip("阶段管理器")] public PhaseManager PhaseMgr;

        [Header("UI")]
        [Tooltip("号角使用按钮")] public Button HornButton;
        [Tooltip("号角进度文本")] public TextMeshProUGUI HornText;

        [Header("调试")]
        [Tooltip("号角当前可用次数（只读）")]
        [SerializeField] private int _hornCount;

        // ────────────── 公开只读属性 ──────────────

        /// <summary>号角当前进度</summary>
        public int HornProgress { get; private set; }
        /// <summary>号角当前可用次数</summary>
        public int HornCount => _hornCount;

        // ────────────── 内部状态 ──────────────

        private bool _isBattleBoardPhase;

        // ────────────── 生命周期 ──────────────

        private void OnEnable()
        {
            GameEvents.OnTileCleared += OnTileCleared;
            GameEvents.OnPhaseChanged += OnPhaseChanged;

            if (HornButton != null)
                HornButton.onClick.AddListener(OnHornButtonClicked);

            // 订阅战斗结束事件，确保战斗结束后按钮禁用
            if (BattleMgr != null)
                BattleMgr.OnBattleEnded += OnBattleEnded;
        }

        private void OnDisable()
        {
            GameEvents.OnTileCleared -= OnTileCleared;
            GameEvents.OnPhaseChanged -= OnPhaseChanged;

            if (HornButton != null)
                HornButton.onClick.RemoveListener(OnHornButtonClicked);

            if (BattleMgr != null)
                BattleMgr.OnBattleEnded -= OnBattleEnded;
        }

        private void Start()
        {
            ResetHorn();

            // 补订阅战斗结束事件（OnEnable 时 BattleMgr 可能还未初始化）
            if (BattleMgr != null)
                BattleMgr.OnBattleEnded += OnBattleEnded;
        }

        // ────────────── 阶段切换 ──────────────

        private void OnPhaseChanged(GamePhase newPhase)
        {
            bool wasBattle = _isBattleBoardPhase;
            _isBattleBoardPhase = (newPhase == GamePhase.Battle);

            // 进入 Battle 阶段或 LastChance 阶段 → 刷新按钮（可能已有 HornCount）
            if (newPhase == GamePhase.Battle || newPhase == GamePhase.LastChance)
            {
                RefreshDisplay();
                return;
            }

            // 离开战斗阶段 → 禁用按钮
            if (wasBattle && !_isBattleBoardPhase)
                SetButtonInteractable(false);
        }

        /// <summary>战斗结束事件：禁用号角按钮</summary>
        private void OnBattleEnded(bool alliesWon)
        {
            SetButtonInteractable(false);
        }

        // ────────────── 方块消除监听 ──────────────

        private void OnTileCleared(TileType type, int count, List<Vector3> worldPositions)
        {
            if (!_isBattleBoardPhase) return;
            if (type != TileType.Horn) return;
            if (count <= 0) return;

            AddHornProgress(count * HornProgressPerTile);
        }

        // ────────────── 进度累积 ──────────────

        private void AddHornProgress(int amount)
        {
            if (amount <= 0) return;

            HornProgress += amount;

            // 进度满 → 获得 1 次号角（溢出保留）
            while (HornProgress >= HornMaxProgress)
            {
                HornProgress -= HornMaxProgress;
                _hornCount++;
                Debug.Log($"[BattleHorn] 号角 +1 次！当前次数：{_hornCount}");
            }

            RefreshDisplay();
        }

        // ────────────── 使用号角 ──────────────

        private void OnHornButtonClicked()
        {
            Debug.Log("[BattleHorn] 点击号角按钮");

            if (_hornCount <= 0) return;

            // 检查战斗是否进行中（包括 LastChance 状态）
            if (!IsBattleActive())
            {
                Debug.Log("[BattleHorn] 当前不在战斗中，无法使用号角");
                return;
            }

            if (AllySpawner == null)
            {
                Debug.LogWarning("[BattleHorn] AllySpawner 未设置！");
                return;
            }

            // 生成 HornUnitsPerUse 个援军
            int spawned = 0;
            for (int i = 0; i < HornUnitsPerUse; i++)
            {
                // 传入自定义预制体，null 时 UnitSpawner 内部回退到默认 SoldierPrefab
                bool success = AllySpawner.SpawnReinforcement(HornUnitPrefab);
                if (success)
                {
                    spawned++;
                }
                else
                {
                    Debug.LogWarning($"[BattleHorn] 第 {i + 1} 个援军生成失败（船上容量已满），停止生成");
                    break;
                }
            }

            // 至少生成 1 个才消耗号角
            if (spawned > 0)
            {
                _hornCount--;
                // 通知 BattleManager 有援军正在赶来（防止援军在路上时误判失败）
                if (BattleMgr != null)
                    BattleMgr.NotifyReinforcementIncoming(spawned);
                Debug.Log($"[BattleHorn] 号角召唤援军！生成 {spawned}/{HornUnitsPerUse}，剩余次数：{_hornCount}");
            }

            RefreshDisplay();
        }

        // ────────────── UI 刷新 ──────────────

        private void RefreshDisplay()
        {
            if (HornText != null)
                HornText.text = $"号角：{HornProgress} / {HornMaxProgress}  次数：{_hornCount}";

            // 按钮可交互 = 有号角次数 且 战斗进行中 且 处于战斗棋盘阶段
            bool canUse = _hornCount > 0 && IsBattleActive();
            SetButtonInteractable(canUse);
        }

        private void SetButtonInteractable(bool interactable)
        {
            if (HornButton != null)
                HornButton.interactable = interactable;
        }

        /// <summary>当前是否处于可使用号角的战斗状态（包括 LastChance）</summary>
        private bool IsBattleActive()
        {
            return BattleMgr != null && (BattleMgr.IsBattleActive || BattleMgr.IsLastChance);
        }

        // ────────────── 重置 ──────────────

        /// <summary>重置所有号角状态（进入新战斗时调用）</summary>
        public void ResetHorn()
        {
            HornProgress = 0;
            _hornCount = 0;
            _isBattleBoardPhase = false;
            SetButtonInteractable(false);
        }
    }
}
