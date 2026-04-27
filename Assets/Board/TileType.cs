using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 方块类型枚举。
    /// 航行阶段第一版：Sword / Gold / Wood / Stone。
    /// 后期新增方块类型只需在此添加枚举值 + GameConfig 中添加对应权重/颜色，
    /// 逻辑代码无需修改。
    /// </summary>
    public enum TileType
    {
        Sword = 0,  // 剑 🔵（消除产剑士）
        Gold  = 1,  // 金币 🟡（消除加资源）
        Wood  = 2,  // 木材 🟢（消除加资源）
        Stone = 3   // 石料 🔴（消除加资源）
    }
}
