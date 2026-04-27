using TMPro;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 资源 UI 显示：监听 ResourceManager.OnResourceChanged 事件，刷新 TextMeshProUGUI 文字。
    ///
    /// 挂载位置：Canvas_Resource 或 ResourcePanel 上。
    ///
    /// Inspector 引用：
    ///   - ResourceManager：场景中的 ResourceManager 组件
    ///   - GoldText：金币数量 TextMeshProUGUI
    ///   - WoodText：木材数量 TextMeshProUGUI
    ///   - StoneText：石料数量 TextMeshProUGUI
    ///
    /// 显示格式：
    ///   金币：0
    ///   木材：0
    ///   石料：0
    ///
    /// 不使用 Update 每帧刷新，只在资源变化时刷新。
    /// 不使用 GameObject.Find，引用全靠 Inspector 手动拖。
    /// </summary>
    public class ResourceUI : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("资源管理器")] public ResourceManager ResourceManager;
        [Tooltip("金币数量文本")] public TextMeshProUGUI GoldText;
        [Tooltip("木材数量文本")] public TextMeshProUGUI WoodText;
        [Tooltip("石料数量文本")] public TextMeshProUGUI StoneText;

        private void OnEnable()
        {
            // 校验引用
            ValidateReferences();

            // 订阅事件
            if (ResourceManager != null)
                ResourceManager.OnResourceChanged += RefreshDisplay;

            // 首次刷新
            RefreshDisplay();
        }

        private void OnDisable()
        {
            // 取消订阅
            if (ResourceManager != null)
                ResourceManager.OnResourceChanged -= RefreshDisplay;
        }

        /// <summary>
        /// 刷新 UI 显示。只在资源变化时调用，不每帧 Update。
        /// </summary>
        private void RefreshDisplay()
        {
            if (ResourceManager == null) return;

            if (GoldText != null)
                GoldText.text = $"金币：{ResourceManager.Gold}";
            if (WoodText != null)
                WoodText.text = $"木材：{ResourceManager.Wood}";
            if (StoneText != null)
                StoneText.text = $"石料：{ResourceManager.Stone}";
        }

        /// <summary>
        /// 校验 Inspector 引用，缺失的用 Debug.LogError 提示。
        /// </summary>
        private void ValidateReferences()
        {
            if (ResourceManager == null)
                Debug.LogError("[ResourceUI] ResourceManager 引用未设置！请在 Inspector 中拖入。");
            if (GoldText == null)
                Debug.LogError("[ResourceUI] GoldText 引用未设置！请在 Inspector 中拖入。");
            if (WoodText == null)
                Debug.LogError("[ResourceUI] WoodText 引用未设置！请在 Inspector 中拖入。");
            if (StoneText == null)
                Debug.LogError("[ResourceUI] StoneText 引用未设置！请在 Inspector 中拖入。");
        }
    }
}
