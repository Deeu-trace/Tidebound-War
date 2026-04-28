using UnityEngine;
using UnityEngine.UI;

namespace TideboundWar
{
    /// <summary>
    /// 开始菜单控制器：游戏启动时显示开始界面，点击开始后进入正常航行阶段。
    ///
    /// 挂载位置：Canvas_StartMenu 根对象上。
    ///
    /// 流程：
    ///   1. 游戏启动 → Canvas_StartMenu 显示，棋盘隐藏/锁定，航行暂停
    ///   2. 点击 StartButton → 隐藏菜单 → 显示航行棋盘 → 重置航行进度 → 允许三消
    ///   3. 点击 QuitButton → 退出游戏（打包后）或输出日志（Editor中）
    /// </summary>
    public class StartMenuController : MonoBehaviour
    {
        [Header("UI 引用")]
        [Tooltip("开始按钮")] public Button StartButton;
        [Tooltip("退出按钮（可选）")] public Button QuitButton;
        [Tooltip("开始菜单 Canvas 根对象（点击开始后隐藏）")]
        public GameObject MenuRoot;

        [Header("系统引用")]
        [Tooltip("棋盘阶段控制器（点击开始后显示航行棋盘）")]
        public BoardPhaseController BoardPhaseCtrl;
        [Tooltip("航行进度管理器（点击开始后重置航行进度）")]
        public VoyageProgressManager VoyageProgressMgr;

        private void OnEnable()
        {
            if (StartButton != null)
                StartButton.onClick.AddListener(OnStartButtonClicked);

            if (QuitButton != null)
                QuitButton.onClick.AddListener(OnQuitButtonClicked);
        }

        private void OnDisable()
        {
            if (StartButton != null)
                StartButton.onClick.RemoveListener(OnStartButtonClicked);

            if (QuitButton != null)
                QuitButton.onClick.RemoveListener(OnQuitButtonClicked);
        }

        private void Start()
        {
            // 确保菜单显示
            if (MenuRoot != null)
                MenuRoot.SetActive(true);

            Debug.Log("[StartMenu] 游戏启动，显示开始菜单");
        }

        /// <summary>
        /// 点击开始按钮：隐藏菜单，进入航行阶段。
        /// </summary>
        private void OnStartButtonClicked()
        {
            Debug.Log("[StartMenu] 点击开始按钮");

            // 1. 隐藏开始菜单
            if (MenuRoot != null)
                MenuRoot.SetActive(false);

            // 2. 显示航行棋盘（Sword / Gold / Wood / Stone）
            if (BoardPhaseCtrl != null)
            {
                BoardPhaseCtrl.ShowVoyageBoard();
            }
            else
            {
                Debug.LogWarning("[StartMenu] BoardPhaseController 未设置，无法显示航行棋盘");
            }

            // 3. 重置航行进度，允许开始积累
            if (VoyageProgressMgr != null)
            {
                VoyageProgressMgr.ResetVoyage();
            }
            else
            {
                Debug.LogWarning("[StartMenu] VoyageProgressManager 未设置，无法重置航行进度");
            }

            Debug.Log("[StartMenu] 游戏开始，进入航行阶段");
        }

        /// <summary>
        /// 点击退出按钮：退出游戏。
        /// </summary>
        private void OnQuitButtonClicked()
        {
            Debug.Log("[StartMenu] Quit");

#if UNITY_EDITOR
            // Editor 中无法真正退出，只输出日志
            Debug.Log("[StartMenu] 编辑器中无法退出游戏，仅在打包后生效");
#else
            Application.Quit();
#endif
        }
    }
}
