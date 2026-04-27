using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 游戏阶段枚举
    /// </summary>
    public enum GamePhase
    {
        Preparation,    // 准备阶段：船上三消
        TransitionOut,  // 过渡：船→陆地（演出中）
        Battle,         // 战斗阶段：自动战斗
        TransitionIn,   // 过渡：陆地→船（演出中）
        Victory,        // 胜利
        Defeat          // 失败
    }
}
