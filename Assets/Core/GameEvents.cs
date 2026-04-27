using System;
using System.Collections.Generic;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 全局 C# 事件定义。
    /// 所有跨系统通信的事件统一在此声明。
    /// </summary>
    public static class GameEvents
    {
        // ── 阶段事件 ──
        public static event Action<GamePhase> OnPhaseChanged;

        // ── 棋盘事件 ──
        public static event Action OnBoardAnimationComplete;
        public static event Action<TileType, int> OnMatchResolved;
        /// <summary>BoardInputController 请求播放棋盘动画（参数：分阶段命令）</summary>
        public static event Action<List<BoardPhase>> OnBoardAnimationRequested;

        // ── 触发器 ──
        public static void PhaseChanged(GamePhase phase) => OnPhaseChanged?.Invoke(phase);
        public static void BoardAnimationComplete() => OnBoardAnimationComplete?.Invoke();
        public static void MatchResolved(TileType type, int count) => OnMatchResolved?.Invoke(type, count);
        public static void BoardAnimationRequested(List<BoardPhase> phases) => OnBoardAnimationRequested?.Invoke(phases);
    }
}
