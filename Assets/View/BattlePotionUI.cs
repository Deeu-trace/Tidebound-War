using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TideboundWar
{
    /// <summary>
    /// 战斗药水 UI：显示攻击/防御药水进度和瓶数，处理按钮点击。
    ///
    /// 挂载位置：Canvas_BattleItems 上。
    ///
    /// Inspector 引用：
    ///   - BattlePotionManager：场景中的 BattlePotionManager 组件
    ///   - AttackPotionText：攻击药水进度/瓶数 TextMeshProUGUI
    ///   - DefensePotionText：防御药水进度/瓶数 TextMeshProUGUI
    ///   - AttackPotionButton：攻击药水使用按钮（UnityEngine.UI.Button）
    ///   - DefensePotionButton：防御药水使用按钮（UnityEngine.UI.Button）
    ///
    /// 显示格式：
    ///   攻击药水：进度 / 最大  瓶数：N
    ///   防御药水：进度 / 最大  瓶数：N
    ///
    /// 不使用 Update 每帧刷新，只在药水状态变化时刷新。
    /// 不使用 GameObject.Find，引用全靠 Inspector 手动拖。
    /// </summary>
    public class BattlePotionUI : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("战斗药水管理器")] public BattlePotionManager BattlePotionManager;
        [Tooltip("攻击药水进度文本")] public TextMeshProUGUI AttackPotionText;
        [Tooltip("防御药水进度文本")] public TextMeshProUGUI DefensePotionText;
        [Tooltip("攻击药水使用按钮")] public Button AttackPotionButton;
        [Tooltip("防御药水使用按钮")] public Button DefensePotionButton;

        [Header("阶段管理")]
        [Tooltip("阶段管理器（LastChance 阶段时禁用药水按钮）")]
        public PhaseManager PhaseMgr;

        private bool _isLastChance;

        private void OnEnable()
        {
            if (BattlePotionManager != null)
                BattlePotionManager.OnPotionChanged += RefreshDisplay;

            if (AttackPotionButton != null)
                AttackPotionButton.onClick.AddListener(OnAttackPotionClicked);

            if (DefensePotionButton != null)
                DefensePotionButton.onClick.AddListener(OnDefensePotionClicked);

            if (PhaseMgr != null)
                PhaseMgr.OnPhaseChanged += OnPhaseChanged;

            RefreshDisplay();
        }

        private void OnDisable()
        {
            if (BattlePotionManager != null)
                BattlePotionManager.OnPotionChanged -= RefreshDisplay;

            if (AttackPotionButton != null)
                AttackPotionButton.onClick.RemoveListener(OnAttackPotionClicked);
            if (DefensePotionButton != null)
                DefensePotionButton.onClick.RemoveListener(OnDefensePotionClicked);

            if (PhaseMgr != null)
                PhaseMgr.OnPhaseChanged -= OnPhaseChanged;
        }

        private void OnPhaseChanged(GamePhase newPhase)
        {
            _isLastChance = (newPhase == GamePhase.LastChance);
            RefreshDisplay();
        }

        /// <summary>
        /// 刷新 UI 显示。只在药水状态变化时调用，不每帧 Update。
        /// </summary>
        private void RefreshDisplay()
        {
            if (BattlePotionManager == null) return;

            if (AttackPotionText != null)
            {
                string buffTag = BattlePotionManager.IsAttackBuffActive ? " [生效中]" : "";
                AttackPotionText.text = $"攻击药水：{BattlePotionManager.AttackPotionProgress} / {BattlePotionManager.AttackPotionMaxProgress}  瓶数：{BattlePotionManager.AttackPotionCount}{buffTag}";
            }

            if (DefensePotionText != null)
            {
                string buffTag = BattlePotionManager.IsDefenseBuffActive ? " [生效中]" : "";
                DefensePotionText.text = $"防御药水：{BattlePotionManager.DefensePotionProgress} / {BattlePotionManager.DefensePotionMaxProgress}  瓶数：{BattlePotionManager.DefensePotionCount}{buffTag}";
            }

            // 按钮可用性：LastChance 时禁用；非 LastChance 时有瓶数才可点击
            if (AttackPotionButton != null)
                AttackPotionButton.interactable = !_isLastChance && BattlePotionManager.AttackPotionCount > 0;
            if (DefensePotionButton != null)
                DefensePotionButton.interactable = !_isLastChance && BattlePotionManager.DefensePotionCount > 0;
        }

        private void OnAttackPotionClicked()
        {
            Debug.Log("[BattlePotion] 点击攻击药水按钮");
            if (BattlePotionManager != null)
                BattlePotionManager.UseAttackPotion();
        }

        private void OnDefensePotionClicked()
        {
            Debug.Log("[BattlePotion] 点击防御药水按钮");
            if (BattlePotionManager != null)
                BattlePotionManager.UseDefensePotion();
        }
    }
}
