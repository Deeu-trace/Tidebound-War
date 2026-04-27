using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 棋盘数据容器。纯数据，不含任何逻辑。
    /// </summary>
    public class BoardData
    {
        private readonly TileType[,] _tiles;
        public int Columns { get; }
        public int Rows { get; }

        public BoardData(int columns, int rows)
        {
            Columns = columns;
            Rows = rows;
            _tiles = new TileType[columns, rows];
        }

        /// <summary>
        /// 获取或设置指定位置的方块类型
        /// </summary>
        public TileType this[int x, int y]
        {
            get
            {
                if (x < 0 || x >= Columns || y < 0 || y >= Rows)
                    throw new System.IndexOutOfRangeException(
                        $"棋盘坐标越界: ({x}, {y}), 尺寸: {Columns}x{Rows}");
                return _tiles[x, y];
            }
            set
            {
                if (x < 0 || x >= Columns || y < 0 || y >= Rows)
                    throw new System.IndexOutOfRangeException(
                        $"棋盘坐标越界: ({x}, {y}), 尺寸: {Columns}x{Rows}");
                _tiles[x, y] = value;
            }
        }

        /// <summary>
        /// 检查坐标是否在棋盘范围内
        /// </summary>
        public bool IsValid(int x, int y)
        {
            return x >= 0 && x < Columns && y >= 0 && y < Rows;
        }

        /// <summary>
        /// 交换两个位置的方块
        /// </summary>
        public void Swap(Vector2Int a, Vector2Int b)
        {
            var temp = this[a.x, a.y];
            this[a.x, a.y] = this[b.x, b.y];
            this[b.x, b.y] = temp;
        }

        /// <summary>
        /// 获取棋盘的深拷贝
        /// </summary>
        public BoardData Clone()
        {
            var clone = new BoardData(Columns, Rows);
            for (int x = 0; x < Columns; x++)
                for (int y = 0; y < Rows; y++)
                    clone[x, y] = _tiles[x, y];
            return clone;
        }
    }
}
