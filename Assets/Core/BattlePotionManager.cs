using System;
using System.Collections.Generic;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 战斗药水管理器：管理攻击药水 / 防御药水的进度累积、瓶数、使用、倍率计时。
    ///
    /// 挂载位置：GameManager 或 Systems 空物体上。
    ///
    /// 核心流程：
    ///   1. 战斗棋盘阶段，消除 Buff01 → 增加攻击药水进度
    ///   2. 战斗棋盘阶段，消除 Buff02 → 增加防御药水进度
    ///   3. 进度满后获得一瓶对应药水（溢出进度保留）
    ///   4. 玩家点击 UI 按钮使用药水 → 激活倍率 Buff（持续一段时间）
    ///   5. 再次使用同种药水 → 延长时间，不叠加倍率
    ///
    /// 事件：
    ///   OnPotionChanged — 药水进度/瓶数/倍率状态变化时触发，UI 订阅此事件刷新显示。
    /// </summary>
    public class BattlePotionManager : MonoBehaviour
    {
        // ────────────── Inspector 参数 ──────────────

        [Header("攻击药水参数")]
        [Tooltip("攻击药水进度上限（满后获得一瓶）")] public int AttackPotionMaxProgress = 10;
        [Tooltip("每消除 1 个 Buff01 方块增加多少进度")] public int AttackProgressPerTile = 1;
        [Tooltip("攻击药水持续时间（秒）")] public float AttackPotionDuration = 8f;
        [Tooltip("攻击药水倍率（我方攻击伤害 × 此值）")] public float AttackDamageMultiplier = 1.5f;

        [Header("防御药水参数")]
        [Tooltip("防御药水进度上限（满后获得一瓶）")] public int DefensePotionMaxProgress = 10;
        [Tooltip("每消除 1 个 Buff02 方块增加多少进度")] public int DefenseProgressPerTile = 1;
        [Tooltip("防御药水持续时间（秒）")] public float DefensePotionDuration = 8f;
        [Tooltip("防御药水倍率（我方受到伤害 × 此值，<1 为减伤）")] public float DefenseDamageMultiplier = 0.6f;

        [Header("引用")]
        [Tooltip("阶段管理器（判断当前是否处于战斗阶段）")]
        public PhaseManager PhaseMgr;

        // ────────────── 公开只读属性 ──────────────

        /// <summary>攻击药水当前进度</summary>
        public int AttackPotionProgress { get; private set; }
        /// <summary>攻击药水当前瓶数</summary>
        public int AttackPotionCount { get; private set; }
        /// <summary>攻击药水倍率是否激活中</summary>
        public bool IsAttackBuffActive => _attackBuffRemaining > 0f;
        /// <summary>攻击药水当前倍率（未激活时返回 1）</summary>
        public float CurrentAttackMultiplier => IsAttackBuffActive ? AttackDamageMultiplier : 1f;

        /// <summary>防御药水当前进度</summary>
        public int DefensePotionProgress { get; private set; }
        /// <summary>防御药水当前瓶数</summary>
        public int DefensePotionCount { get; private set; }
        /// <summary>防御药水倍率是否激活中</summary>
        public bool IsDefenseBuffActive => _defenseBuffRemaining > 0f;
        /// <summary>防御药水当前倍率（未激活时返回 1）</summary>
        public float CurrentDefenseMultiplier => IsDefenseBuffActive ? DefenseDamageMultiplier : 1f;

        // ────────────── 事件 ──────────────

        /// <summary>药水状态变化时触发（进度/瓶数/倍率）。UI 订阅此事件刷新显示。</summary>
        public event Action OnPotionChanged;

        // ────────────── 内部状态 ──────────────

        private bool _isBattleBoardPhase;  // 当前是否处于战斗棋盘阶段
        private float _attackBuffRemaining; // 攻击 Buff 剩余秒数
        private float _defenseBuffRemaining; // 防御 Buff 剩余秒数

        // ────────────── 生命周期 ──────────────

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
            ResetAllPotions();
        }

        private void Update()
        {
            bool changed = false;

            // 递减攻击 Buff 计时
            if (_attackBuffRemaining > 0f)
            {
                _attackBuffRemaining -= Time.deltaTime;
                if (_attackBuffRemaining <= 0f)
                {
                    _attackBuffRemaining = 0f;
                    changed = true;
                    Debug.Log("[BattlePotion] 攻击药水效果结束");
                }
            }

            // 递减防御 Buff 计时
            if (_defenseBuffRemaining > 0f)
            {
                _defenseBuffRemaining -= Time.deltaTime;
                if (_defenseBuffRemaining <= 0f)
                {
                    _defenseBuffRemaining = 0f;
                    changed = true;
                    Debug.Log("[BattlePotion] 防御药水效果结束");
                }
            }

            if (changed)
                OnPotionChanged?.Invoke();
        }

        // ────────────── 阶段切换 ──────────────

        private void OnPhaseChanged(GamePhase newPhase)
        {
            if (newPhase == GamePhase.Battle && !_isBattleBoardPhase)
            {
                _isBattleBoardPhase = true;
            }
            else if (newPhase != GamePhase.Battle)
            {
                _isBattleBoardPhase = false;
            }
        }

        // ────────────── 方块消除监听 ──────────────

        private void OnTileCleared(TileType type, int count, List<Vector3> worldPositions)
        {
            if (!_isBattleBoardPhase) return;
            if (count <= 0) return;

            if (type == TileType.Buff01)
            {
                AddAttackProgress(count * AttackProgressPerTile);
            }
            else if (type == TileType.Buff02)
            {
                AddDefenseProgress(count * DefenseProgressPerTile);
            }
        }

        // ────────────── 进度累积 ──────────────

        private void AddAttackProgress(int amount)
        {
            if (amount <= 0) return;

            AttackPotionProgress += amount;

            // 进度满 → 获得一瓶（溢出保留）
            while (AttackPotionProgress >= AttackPotionMaxProgress)
            {
                AttackPotionProgress -= AttackPotionMaxProgress;
                AttackPotionCount++;
                Debug.Log($"[BattlePotion] 攻击药水 +1 瓶！当前瓶数：{AttackPotionCount}");
            }

            OnPotionChanged?.Invoke();
        }

        private void AddDefenseProgress(int amount)
        {
            if (amount <= 0) return;

            DefensePotionProgress += amount;

            // 进度满 → 获得一瓶（溢出保留）
            while (DefensePotionProgress >= DefensePotionMaxProgress)
            {
                DefensePotionProgress -= DefensePotionMaxProgress;
                DefensePotionCount++;
                Debug.Log($"[BattlePotion] 防御药水 +1 瓶！当前瓶数：{DefensePotionCount}");
            }

            OnPotionChanged?.Invoke();
        }

        // ────────────── 使用药水（公开 API，由 UI 按钮调用） ──────────────

        /// <summary>
        /// 使用一瓶攻击药水。
        /// 效果：我方攻击伤害 × AttackDamageMultiplier，持续 AttackPotionDuration 秒。
        /// 如果已有攻击 Buff，则延长时间，不叠加倍率。
        /// </summary>
        /// <returns>是否成功使用（瓶数 > 0）</returns>
        public bool UseAttackPotion()
        {
            if (AttackPotionCount <= 0) return false;

            AttackPotionCount--;
            _attackBuffRemaining = AttackPotionDuration;

            Debug.Log($"[BattlePotion] 使用攻击药水！剩余瓶数：{AttackPotionCount}，倍率：{AttackDamageMultiplier}，持续 {AttackPotionDuration}s");
            OnPotionChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// 使用一瓶防御药水。
        /// 效果：我方受到伤害 × DefenseDamageMultiplier，持续 DefensePotionDuration 秒。
        /// 如果已有防御 Buff，则延长时间，不叠加倍率。
        /// </summary>
        /// <returns>是否成功使用（瓶数 > 0）</returns>
        public bool UseDefensePotion()
        {
            if (DefensePotionCount <= 0) return false;

            DefensePotionCount--;
            _defenseBuffRemaining = DefensePotionDuration;

            Debug.Log($"[BattlePotion] 使用防御药水！剩余瓶数：{DefensePotionCount}，倍率：{DefenseDamageMultiplier}，持续 {DefensePotionDuration}s");
            OnPotionChanged?.Invoke();
            return true;
        }

        // ────────────── 外部接口（供 BattleManager / SimpleUnit 读取倍率） ──────────────

        /// <summary>
        /// 获取当前攻击倍率（友军造成伤害时调用）。
        /// 未激活时返回 1（无变化）。
        /// </summary>
        public float GetAttackMultiplier()
        {
            return CurrentAttackMultiplier;
        }

        /// <summary>
        /// 获取当前防御倍率（友军受到伤害时调用）。
        /// 未激活时返回 1（无变化）。
        /// </summary>
        public float GetDefenseMultiplier()
        {
            return CurrentDefenseMultiplier;
        }

        // ────────────── 重置 ──────────────

        /// <summary>重置所有药水状态（进入新战斗时调用）</summary>
        public void ResetAllPotions()
        {
            AttackPotionProgress = 0;
            AttackPotionCount = 0;
            _attackBuffRemaining = 0f;

            DefensePotionProgress = 0;
            DefensePotionCount = 0;
            _defenseBuffRemaining = 0f;

            _isBattleBoardPhase = false;

            OnPotionChanged?.Invoke();
        }
    }
}
