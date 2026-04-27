using System;
using System.Collections.Generic;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 棋盘命令基类。
    /// BoardSystem 输出有序命令队列，BoardView 消费命令做动画。
    /// </summary>
    public abstract class BoardCommand { }

    /// <summary>
    /// 交换动画命令：两个方块互换位置
    /// </summary>
    public class SwapCommand : BoardCommand
    {
        public Vector2Int PosA;
        public Vector2Int PosB;
        public TileType TypeA;
        public TileType TypeB;
    }

    /// <summary>
    /// 回退交换动画命令：交换无效时播放回退
    /// </summary>
    public class SwapBackCommand : BoardCommand
    {
        public Vector2Int PosA;
        public Vector2Int PosB;
        public TileType TypeA;
        public TileType TypeB;
    }

    /// <summary>
    /// 消除动画命令：方块被消除
    /// </summary>
    public class RemoveCommand : BoardCommand
    {
        public Vector2Int Pos;
        public TileType Type;
    }

    /// <summary>
    /// 下落动画命令：方块从高处下落到低处
    /// </summary>
    public class FallCommand : BoardCommand
    {
        public Vector2Int From;
        public Vector2Int To;
        public TileType Type;
    }

    /// <summary>
    /// 生成新方块动画命令：顶部补充新方块
    /// </summary>
    public class SpawnCommand : BoardCommand
    {
        public Vector2Int Pos;
        public TileType Type;
        /// <summary>该方块从棋盘上方第几行开始下落</summary>
        public int SpawnRow;
    }

    /// <summary>
    /// 洗牌动画命令：整个棋盘重新排列
    /// </summary>
    public class ShuffleCommand : BoardCommand
    {
        public List<(Vector2Int pos, TileType type)> Tiles;
    }

    /// <summary>
    /// 动画阶段：同一阶段内的所有命令同时播放，不同阶段顺序播放。
    /// 这是解决动画时序问题的核心结构。
    /// 
    /// 示例：一次匹配的命令阶段如下：
    ///   Phase1(Swap) → Phase2(Remove×3) → Phase3(Fall×2) → Phase4(Spawn×3)
    /// 每个阶段内的动画同时播放，阶段之间顺序播放。
    /// </summary>
    public class BoardPhase
    {
        public List<BoardCommand> Commands = new List<BoardCommand>();

        public BoardPhase() { }

        public BoardPhase(BoardCommand cmd)
        {
            Commands.Add(cmd);
        }

        public void Add(BoardCommand cmd) => Commands.Add(cmd);
    }
}
