using System;
using UnityEngine;
using DG.Tweening;

namespace TideboundWar
{
    /// <summary>
    /// Debug 控制台：消费 Debug Action Map 的事件。
    /// 按 F5 洗牌、F6 切换阶段。
    /// </summary>
    public class DebugConsole : MonoBehaviour
    {
        [SerializeField] private PlayerInputRouter _inputRouter;
        [SerializeField] private BoardSystem _boardSystem;
        [SerializeField] private PhaseManager _phaseManager;
        [SerializeField] private BoardInputController _inputController;

        private void OnEnable()
        {
            if (_inputRouter != null)
            {
                _inputRouter.OnShuffleBoard += OnShuffleBoard;
                _inputRouter.OnNextPhase += OnNextPhase;
            }
        }

        private void OnDisable()
        {
            if (_inputRouter != null)
            {
                _inputRouter.OnShuffleBoard -= OnShuffleBoard;
                _inputRouter.OnNextPhase -= OnNextPhase;
            }
        }

        private void OnShuffleBoard()
        {
            Debug.Log("[Debug] ═══ F5 - 重新初始化棋盘 ═══");
            
            // 强制解锁，防止动画卡死导致输入永久锁定
            _boardSystem?.SetProcessing(false);
            
            // 强制解锁输入控制器
            if (_inputController != null)
                _inputController.IsAnimating = false;

            // 杀掉所有进行中的棋盘动画
            DOTween.Kill(TweenManager.Tags.Board);
            
            _boardSystem?.InitializeBoard();

            Debug.Log("[Debug] ═══ F5 完成 ═══");
        }

        private void OnNextPhase()
        {
            Debug.Log("[Debug] F6 - 切换到下一阶段");
            _phaseManager?.NextPhase();
        }
    }
}
