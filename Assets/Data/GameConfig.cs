using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 全局可调参数配置。
    /// 所有数值集中在此，Inspector 可调，不硬编码。
    /// 
    /// 创建方式：Assets/Data/ 右键 → Create → 三消军团 → GameConfig
    /// </summary>
    [CreateAssetMenu(fileName = "GameConfig", menuName = "三消军团/GameConfig")]
    public class GameConfig : ScriptableObject
    {
        [Header("棋盘设置")]
        [Tooltip("棋盘列数")] public int BoardColumns = 5;
        [Tooltip("棋盘行数")] public int BoardRows = 8;
        [Tooltip("方块尺寸（像素）")] public float TileSize = 80f;
        [Tooltip("拖拽识别阈值（像素）")] public float DragThreshold = 30f;
        [Tooltip("最小匹配数")] public int MatchMin = 3;

        [Header("方块权重")]
        [Tooltip("各方块生成权重：剑/金币/木材/石料")] 
        public float[] TileWeights = { 0.30f, 0.25f, 0.25f, 0.20f };

        [Header("方块颜色（Demo用）")]
        public Color SwordColor = new Color(0.2f, 0.4f, 0.9f);   // 蓝色
        public Color GoldColor  = new Color(0.9f, 0.8f, 0.1f);   // 黄色
        public Color WoodColor  = new Color(0.2f, 0.8f, 0.3f);   // 绿色
        public Color StoneColor = new Color(0.7f, 0.5f, 0.3f);   // 棕色

        [Header("方块标签（Demo用文字）")]
        public string SwordLabel = "剑";
        public string GoldLabel  = "金";
        public string WoodLabel  = "木";
        public string StoneLabel = "石";

        [Header("资源倍率")]
        [Tooltip("每个资源方块消除后加多少资源（消3个Gold × 此值 = 加Gold数量）")]
        public int ResourcePerTile = 1;

        [Header("动画时长")]
        [Tooltip("交换动画(秒)")] public float SwapAnimDuration = 0.2f;
        [Tooltip("消除动画(秒)")] public float RemoveAnimDuration = 0.15f;
        [Tooltip("下落一格动画(秒)")] public float FallAnimDuration = 0.12f;
        [Tooltip("生成新方块动画(秒)")] public float SpawnAnimDuration = 0.2f;
        [Tooltip("洗牌动画(秒)")] public float ShuffleAnimDuration = 0.5f;

        // ── 便捷方法 ──

        /// <summary>获取方块类型对应的 Demo 颜色</summary>
        public Color GetTileColor(TileType type)
        {
            return type switch
            {
                TileType.Sword => SwordColor,
                TileType.Gold  => GoldColor,
                TileType.Wood  => WoodColor,
                TileType.Stone => StoneColor,
                _ => Color.white
            };
        }

        /// <summary>获取方块类型对应的 Demo 文字标签</summary>
        public string GetTileLabel(TileType type)
        {
            return type switch
            {
                TileType.Sword => SwordLabel,
                TileType.Gold  => GoldLabel,
                TileType.Wood  => WoodLabel,
                TileType.Stone => StoneLabel,
                _ => "?"
            };
        }
    }
}
