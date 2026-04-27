using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 方块类型枚举。
    /// 航行阶段：Sword / Gold / Wood / Stone。
    /// 战斗阶段：MonsterTide / Horn / Buff01 / Buff02。
    /// 后续新增方块类型只需在此添加枚举值 + GameConfig 中添加对应权重/颜色，
    /// 逻辑代码无需修改。
    /// </summary>
    public enum TileType
    {
        Sword      = 0,  // 剑 🔵（航行：消除产剑士）
        Gold       = 1,  // 金币 🟡（航行：消除加资源）
        Wood       = 2,  // 木材 🟢（航行：消除加资源）
        Stone      = 3,  // 石料 🔴（航行：消除加资源）
        MonsterTide = 4, // 怪物潮 🟣（战斗：暂无具体效果）
        Horn        = 5, // 号角 🟠（战斗：暂无具体效果）
        Buff01      = 6, // 增益01 🩵（战斗：暂无具体效果）
        Buff02      = 7  // 增益02 🩷（战斗：暂无具体效果）
    }
}
