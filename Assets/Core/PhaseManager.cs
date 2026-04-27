using System;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 阶段状态机。
    /// 管理 GamePhase 枚举、阶段切换事件、以及各阶段下哪些输入可用。
    /// 不引用 DOTween，不引用 View 层。
    /// </summary>
    public class PhaseManager : MonoBehaviour
    {
        [SerializeField] private GamePhase _currentPhase = GamePhase.Preparation;

        /// <summary>当前游戏阶段</summary>
        public GamePhase CurrentPhase => _currentPhase;

        /// <summary>阶段切换事件</summary>
        public event Action<GamePhase> OnPhaseChanged;

        /// <summary>是否允许三消输入</summary>
        public bool CanAcceptBoardInput => _currentPhase == GamePhase.Preparation;

        /// <summary>
        /// 切换到指定阶段
        /// </summary>
        public void SetPhase(GamePhase newPhase)
        {
            if (_currentPhase == newPhase) return;

            var oldPhase = _currentPhase;
            _currentPhase = newPhase;

            Debug.Log($"[PhaseManager] 阶段切换: {oldPhase} → {newPhase}");

            OnPhaseChanged?.Invoke(newPhase);
            GameEvents.PhaseChanged(newPhase);
        }

        /// <summary>
        /// 切换到下一阶段（按 Preparation → TransitionOut → Battle → TransitionIn → Preparation 循环）
        /// </summary>
        public void NextPhase()
        {
            switch (_currentPhase)
            {
                case GamePhase.Preparation:
                    SetPhase(GamePhase.TransitionOut);
                    break;
                case GamePhase.TransitionOut:
                    SetPhase(GamePhase.Battle);
                    break;
                case GamePhase.Battle:
                    SetPhase(GamePhase.TransitionIn);
                    break;
                case GamePhase.TransitionIn:
                    SetPhase(GamePhase.Preparation);
                    break;
                case GamePhase.Victory:
                case GamePhase.Defeat:
                    Debug.Log("[PhaseManager] 游戏已结束，无法切换下一阶段");
                    break;
            }
        }
    }
}
