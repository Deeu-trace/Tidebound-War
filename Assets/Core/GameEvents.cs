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

        /// <summary>
        /// 每轮消除动画完成后触发（不是逻辑计算阶段）。
        /// 连锁时每轮消除动画播完都会触发一次，航行进度随每轮逐步增加。
        /// 参数：方块类型、消除数量、被消除方块的世界坐标列表。
        /// </summary>
        public static event Action<TileType, int, List<Vector3>> OnTileCleared;

        /// <summary>
        /// 本次操作的所有消除、下落、补充、连锁动画全部结束后触发。
        /// 适合做延迟结算（如航行满后等动画结束再触发岛屿）。
        /// </summary>
        public static event Action OnBoardResolveComplete;

        /// <summary>BoardInputController 请求播放棋盘动画（参数：分阶段命令）</summary>
        public static event Action<List<BoardPhase>> OnBoardAnimationRequested;

        // ── 触发器 ──
        public static void PhaseChanged(GamePhase phase) => OnPhaseChanged?.Invoke(phase);
        public static void TileCleared(TileType type, int count, List<Vector3> worldPositions) => OnTileCleared?.Invoke(type, count, worldPositions);
        public static void BoardResolveComplete() => OnBoardResolveComplete?.Invoke();
        public static void BoardAnimationRequested(List<BoardPhase> phases) => OnBoardAnimationRequested?.Invoke(phases);
    }
}
