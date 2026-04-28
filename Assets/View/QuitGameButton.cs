using UnityEngine;
using UnityEngine.UI;

namespace TideboundWar
{
    /// <summary>
    /// 退出游戏按钮：点击后退出应用程序。
    /// 可挂在任何带 Button 组件的物体上，或单独挂在一个管理器空物体上。
    /// </summary>
    public class QuitGameButton : MonoBehaviour
    {
        [Tooltip("退出按钮（如挂在同物体上可不填，自动获取）")]
        public Button Button;

        private void OnEnable()
        {
            if (Button == null)
                Button = GetComponent<Button>();

            if (Button != null)
                Button.onClick.AddListener(OnQuitClicked);
        }

        private void OnDisable()
        {
            if (Button != null)
                Button.onClick.RemoveListener(OnQuitClicked);
        }

        private void OnQuitClicked()
        {
            Debug.Log("[QuitGame] 退出游戏");

#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
