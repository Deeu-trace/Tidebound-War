using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 棋盘阶段控制器：根据游戏流程切换棋盘显示和方块类型。
    ///
    /// 三个公开方法：
    ///   - HideBoard()：隐藏棋盘，禁止玩家操作
    ///   - ShowVoyageBoard()：显示航行棋盘（Sword / Gold / Wood / Stone），重新生成
    ///   - ShowBattleBoard()：显示战斗棋盘（MonsterTide / Horn / Buff01 / Buff02），重新生成
    ///
    /// 调用时机：
    ///   - 岛屿开始下落时 → HideBoard()
    ///   - 木板伸出完成后 → ShowBattleBoard()
    ///   - 战斗结束时 → HideBoard()
    ///   - 岛屿准备离开时 → ShowVoyageBoard()
    ///
    /// 挂载位置：与 BoardView 同物体，或 GameManager / Systems 空物体上
    /// </summary>
    public class BoardPhaseController : MonoBehaviour
    {
        [Header("引用")]
        [Tooltip("棋盘系统（切换方块类型 + 重新初始化）")]
        public BoardSystem BoardSystem;
        [Tooltip("棋盘视图（显示/隐藏棋盘 GameObject）")]
        public BoardView BoardView;
        [Tooltip("游戏配置（获取航行/战斗方块权重）")]
        public GameConfig GameConfig;
        [Tooltip("阶段管理器（隐藏时锁定输入）")]
        public PhaseManager PhaseManager;

        // ── 预计算的方块类型数组 ──
        private static readonly TileType[] VoyageTileTypes = 
            { TileType.Sword, TileType.Gold, TileType.Wood, TileType.Stone };
        private static readonly TileType[] BattleTileTypes = 
            { TileType.MonsterTide, TileType.Horn, TileType.Buff01, TileType.Buff02 };

        /// <summary>当前棋盘是否处于隐藏状态</summary>
        public bool IsHidden { get; private set; }

        /// <summary>
        /// 隐藏棋盘，禁止玩家操作。
        /// </summary>
        public void HideBoard()
        {
            if (BoardView != null)
                BoardView.gameObject.SetActive(false);

            if (PhaseManager != null)
                PhaseManager.SetPhase(GamePhase.TransitionOut);

            IsHidden = true;
            Debug.Log("[BoardPhase] 棋盘已隐藏");
        }

        /// <summary>
        /// 显示航行棋盘（Sword / Gold / Wood / Stone），重新生成所有方块。
        /// </summary>
        public void ShowVoyageBoard()
        {
            // 1. 设置方块类型和权重
            if (BoardSystem != null && GameConfig != null)
            {
                BoardSystem.SetActiveTileTypes(VoyageTileTypes, GameConfig.TileWeights);
                BoardSystem.InitializeBoard();
            }

            // 2. 显示棋盘
            if (BoardView != null)
                BoardView.gameObject.SetActive(true);

            // 3. 允许玩家操作
            if (PhaseManager != null)
                PhaseManager.SetPhase(GamePhase.Preparation);

            IsHidden = false;
            Debug.Log("[BoardPhase] 显示航行棋盘（剑/金/木/石）");
        }

        /// <summary>
        /// 显示战斗棋盘（MonsterTide / Horn / Buff01 / Buff02），重新生成所有方块。
        /// </summary>
        public void ShowBattleBoard()
        {
            // 1. 设置方块类型和权重
            if (BoardSystem != null && GameConfig != null)
            {
                BoardSystem.SetActiveTileTypes(BattleTileTypes, GameConfig.BattleTileWeights);
                BoardSystem.InitializeBoard();
            }

            // 2. 显示棋盘
            if (BoardView != null)
                BoardView.gameObject.SetActive(true);

            // 3. 允许玩家操作（战斗阶段也可以三消）
            if (PhaseManager != null)
                PhaseManager.SetPhase(GamePhase.Battle);

            IsHidden = false;
            Debug.Log("[BoardPhase] 显示战斗棋盘（潮/角/增/护）");
        }
    }
}
