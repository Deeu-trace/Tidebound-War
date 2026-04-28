using UnityEngine;
using UnityEngine.UI;

namespace TideboundWar
{
    /// <summary>
    /// 暂停菜单控制器：按 ESC 或 Cancel 键暂停/恢复游戏。
    ///
    /// 挂载位置：Canvas_PauseMenu 根对象上。
    ///
    /// 流程：
    ///   1. 游戏运行中按 ESC → Time.timeScale = 0，显示暂停菜单
    ///   2. 点击继续 → Time.timeScale = 1，隐藏菜单
    ///   3. 点击回到主菜单 → Time.timeScale = 1，显示开始菜单
    ///   4. 点击退出游戏 → 退出应用程序
    ///
    /// 注意：
    ///   - 暂停时不修改 PhaseManager 阶段，仅冻结时间
    ///   - 暂停菜单初始状态应为隐藏（PauseRoot = inactive）
    /// </summary>
    public class PauseMenuController : MonoBehaviour
    {
        [Header("UI 引用")]
        [Tooltip("暂停菜单根对象（显示/隐藏整个面板）")]
        public GameObject PauseRoot;

        [Tooltip("继续游戏按钮")]
        public Button ResumeButton;

        [Tooltip("回到主菜单按钮")]
        public Button MainMenuButton;

        [Tooltip("退出游戏按钮")]
        public Button QuitButton;

        [Header("系统引用")]
        [Tooltip("开始菜单控制器（回到主菜单时需要）")]
        public StartMenuController StartMenuCtrl;

        [Tooltip("棋盘阶段控制器（回到主菜单时隐藏棋盘）")]
        public BoardPhaseController BoardPhaseCtrl;

        // ── 内部状态 ──
        private bool _isPaused;

        /// <summary>当前是否处于暂停状态</summary>
        public bool IsPaused => _isPaused;

        private void OnEnable()
        {
            if (ResumeButton != null)
                ResumeButton.onClick.AddListener(OnResumeClicked);
            if (MainMenuButton != null)
                MainMenuButton.onClick.AddListener(OnMainMenuClicked);
            if (QuitButton != null)
                QuitButton.onClick.AddListener(OnQuitClicked);

            GameEvents.OnPhaseChanged += OnPhaseChanged;
        }

        private void OnDisable()
        {
            if (ResumeButton != null)
                ResumeButton.onClick.RemoveListener(OnResumeClicked);
            if (MainMenuButton != null)
                MainMenuButton.onClick.RemoveListener(OnMainMenuClicked);
            if (QuitButton != null)
                QuitButton.onClick.RemoveListener(OnQuitClicked);

            GameEvents.OnPhaseChanged -= OnPhaseChanged;
        }

        private void Start()
        {
            // 确保暂停菜单初始隐藏
            if (PauseRoot != null)
                PauseRoot.SetActive(false);

            _isPaused = false;
        }

        private void Update()
        {
            // 按 ESC 键切换暂停
            if (Input.GetKeyDown(KeyCode.Escape))
            {
                if (_isPaused)
                    Resume();
                else
                    Pause();
            }
        }

        // ── 公开方法 ──

        /// <summary>暂停游戏</summary>
        public void Pause()
        {
            if (_isPaused) return;

            _isPaused = true;
            Time.timeScale = 0f;

            if (PauseRoot != null)
                PauseRoot.SetActive(true);

            Debug.Log("[PauseMenu] 游戏暂停");
        }

        /// <summary>恢复游戏</summary>
        public void Resume()
        {
            if (!_isPaused) return;

            _isPaused = false;
            Time.timeScale = 1f;

            if (PauseRoot != null)
                PauseRoot.SetActive(false);

            Debug.Log("[PauseMenu] 游戏恢复");
        }

        // ── 按钮回调 ──

        /// <summary>点击继续按钮</summary>
        private void OnResumeClicked()
        {
            Resume();
        }

        /// <summary>点击回到主菜单按钮</summary>
        private void OnMainMenuClicked()
        {
            Debug.Log("[PauseMenu] 回到主菜单");

            // 1. 先恢复时间，否则后续逻辑可能卡住
            Time.timeScale = 1f;
            _isPaused = false;

            // 2. 隐藏暂停菜单
            if (PauseRoot != null)
                PauseRoot.SetActive(false);

            // 3. 隐藏棋盘
            if (BoardPhaseCtrl != null)
                BoardPhaseCtrl.HideBoard();

            // 4. 显示开始菜单
            if (StartMenuCtrl != null && StartMenuCtrl.MenuRoot != null)
                StartMenuCtrl.MenuRoot.SetActive(true);
            else
                Debug.LogWarning("[PauseMenu] StartMenuController 或 MenuRoot 未设置，无法显示开始菜单");
        }

        /// <summary>点击退出游戏按钮</summary>
        private void OnQuitClicked()
        {
            Debug.Log("[PauseMenu] 退出游戏");

            // 恢复时间再退出，避免 Editor 卡住
            Time.timeScale = 1f;

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        // ── 事件回调 ──

        /// <summary>
        /// 阶段切换时自动取消暂停（防止阶段切换时仍卡在暂停态）
        /// </summary>
        private void OnPhaseChanged(GamePhase newPhase)
        {
            if (_isPaused)
            {
                Debug.Log($"[PauseMenu] 阶段切换为 {newPhase}，自动取消暂停");
                Resume();
            }
        }
    }
}
