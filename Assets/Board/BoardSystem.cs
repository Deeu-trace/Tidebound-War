using System;
using System.Collections.Generic;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 棋盘核心逻辑系统。纯 C# 逻辑，不引用 DOTween，不引用任何 View 组件。
    /// 所有视觉反馈通过 BoardPhase 分阶段命令输出，由 BoardView 消费。
    /// </summary>
    public class BoardSystem : MonoBehaviour
    {
        [SerializeField] private GameConfig _gameConfig;

        private BoardData _boardData;
        private System.Random _rng;
        private bool _isProcessing;

        public bool IsProcessing => _isProcessing;
        public BoardData BoardData => _boardData;

        /// <summary>棋盘初始化完成后触发，参数为分阶段命令</summary>
        public event Action<List<BoardPhase>> OnBoardInitialized;
        /// <summary>交换后触发，参数为分阶段命令</summary>
        public event Action<List<BoardPhase>> OnCommandsGenerated;

        private void Start()
        {
            InitializeBoard();
        }

        #region 初始化

        public void InitializeBoard()
        {
            int cols = _gameConfig.BoardColumns;
            int rows = _gameConfig.BoardRows;
            _boardData = new BoardData(cols, rows);
            _rng = new System.Random();
            _isProcessing = false;

            for (int x = 0; x < cols; x++)
            {
                for (int y = 0; y < rows; y++)
                {
                    TileType type;
                    do
                    {
                        type = RandomTileType();
                    } while (WouldCreateMatch(x, y, type));

                    _boardData[x, y] = type;
                }
            }

            Debug.Log($"[BoardSystem] 棋盘初始化: {cols}x{rows}");

            var phases = new List<BoardPhase>();
            var spawnPhase = new BoardPhase();
            for (int x = 0; x < cols; x++)
            {
                for (int y = 0; y < rows; y++)
                {
                    spawnPhase.Add(new SpawnCommand
                    {
                        Pos = new Vector2Int(x, y),
                        Type = _boardData[x, y],
                        SpawnRow = y
                    });
                }
            }
            phases.Add(spawnPhase);

            OnBoardInitialized?.Invoke(phases);
        }

        #endregion

        #region 交换

        public List<BoardPhase> RequestSwap(Vector2Int posA, Vector2Int posB)
        {
            if (_isProcessing) return null;
            if (!IsAdjacent(posA, posB)) return null;

            var typeA = _boardData[posA.x, posA.y];
            var typeB = _boardData[posB.x, posB.y];

            var phases = new List<BoardPhase>();

            // Phase 0：交换动画
            _boardData.Swap(posA, posB);
            phases.Add(new BoardPhase(new SwapCommand
            {
                PosA = posA, PosB = posB,
                TypeA = _boardData[posA.x, posA.y],
                TypeB = _boardData[posB.x, posB.y]
            }));

            // 检查匹配
            var matches = FindAllMatches();

            if (matches.Count > 0)
            {
                // 有效交换 → 处理匹配链
                ProcessMatchesChain(matches, phases, 1);
            }
            else
            {
                // 无效交换 → 回退
                _boardData.Swap(posA, posB);
                phases.Add(new BoardPhase(new SwapBackCommand
                {
                    PosA = posA, PosB = posB,
                    TypeA = typeA, TypeB = typeB
                }));
            }

            OnCommandsGenerated?.Invoke(phases);
            return phases;
        }

        private bool IsAdjacent(Vector2Int a, Vector2Int b)
        {
            int dx = Mathf.Abs(a.x - b.x);
            int dy = Mathf.Abs(a.y - b.y);
            return (dx + dy) == 1;
        }

        #endregion

        #region 匹配检测

        private List<List<Vector2Int>> FindAllMatches()
        {
            var allMatches = new List<List<Vector2Int>>();
            var matched = new HashSet<Vector2Int>();

            int cols = _boardData.Columns;
            int rows = _boardData.Rows;

            // 水平匹配
            for (int y = 0; y < rows; y++)
            {
                int x = 0;
                while (x < cols)
                {
                    var match = FindHorizontalMatch(x, y);
                    if (match != null)
                    {
                        allMatches.Add(match);
                        foreach (var pos in match) matched.Add(pos);
                        x = match[match.Count - 1].x + 1;
                    }
                    else
                    {
                        x++;
                    }
                }
            }

            // 垂直匹配
            for (int x = 0; x < cols; x++)
            {
                int y = 0;
                while (y < rows)
                {
                    var match = FindVerticalMatch(x, y);
                    if (match != null)
                    {
                        allMatches.Add(match);
                        foreach (var pos in match) matched.Add(pos);
                        y = match[match.Count - 1].y + 1;
                    }
                    else
                    {
                        y++;
                    }
                }
            }

            return allMatches;
        }

        private List<Vector2Int> FindHorizontalMatch(int startX, int y)
        {
            var type = _boardData[startX, y];
            var match = new List<Vector2Int> { new Vector2Int(startX, y) };

            for (int x = startX + 1; x < _boardData.Columns; x++)
            {
                if (_boardData[x, y] == type)
                    match.Add(new Vector2Int(x, y));
                else
                    break;
            }

            return match.Count >= _gameConfig.MatchMin ? match : null;
        }

        private List<Vector2Int> FindVerticalMatch(int x, int startY)
        {
            var type = _boardData[x, startY];
            var match = new List<Vector2Int> { new Vector2Int(x, startY) };

            for (int y = startY + 1; y < _boardData.Rows; y++)
            {
                if (_boardData[x, y] == type)
                    match.Add(new Vector2Int(x, y));
                else
                    break;
            }

            return match.Count >= _gameConfig.MatchMin ? match : null;
        }

        private bool WouldCreateMatch(int x, int y, TileType type)
        {
            if (x >= 2 &&
                _boardData[x - 1, y] == type &&
                _boardData[x - 2, y] == type)
                return true;

            if (y >= 2 &&
                _boardData[x, y - 1] == type &&
                _boardData[x, y - 2] == type)
                return true;

            return false;
        }

        #endregion

        #region 消除→下落→补格（分阶段输出）

        private void ProcessMatchesChain(List<List<Vector2Int>> matches, List<BoardPhase> phases, int chainDepth)
        {
            var toRemove = new HashSet<Vector2Int>();
            var matchInfo = new Dictionary<TileType, int>();

            foreach (var match in matches)
            {
                var type = _boardData[match[0].x, match[0].y];
                foreach (var pos in match)
                {
                    if (toRemove.Add(pos))
                    {
                        if (!matchInfo.ContainsKey(type))
                            matchInfo[type] = 0;
                        matchInfo[type]++;
                    }
                }
            }


            // ── 触发游戏事件 ──
            foreach (var kv in matchInfo)
            {
                GameEvents.MatchResolved(kv.Key, kv.Value);
            }

            // ── Phase: 消除 ──
            var removePhase = new BoardPhase();
            foreach (var pos in toRemove)
            {
                removePhase.Add(new RemoveCommand
                {
                    Pos = pos,
                    Type = _boardData[pos.x, pos.y]
                });
            }
            phases.Add(removePhase);

            // 数据层清除
            foreach (var pos in toRemove)
                _boardData[pos.x, pos.y] = (TileType)(-1);

            // ── Phase: 下落 ──
            var fallPhase = new BoardPhase();
            ProcessFall(fallPhase);
            if (fallPhase.Commands.Count > 0)
                phases.Add(fallPhase);

            // ── Phase: 补格 ──
            var spawnPhase = new BoardPhase();
            ProcessSpawn(spawnPhase);
            if (spawnPhase.Commands.Count > 0)
                phases.Add(spawnPhase);

            // ── 连锁检测 ──
            var newMatches = FindAllMatches();
            if (newMatches.Count > 0)
            {
                ProcessMatchesChain(newMatches, phases, chainDepth + 1);
            }
            else if (!HasValidMoves())
            {
                var shufflePhase = new BoardPhase();
                ProcessShuffle(shufflePhase);
                phases.Add(shufflePhase);
                Debug.Log("[BoardSystem] 无有效移动，洗牌");
            }
        }

        private void ProcessFall(BoardPhase phase)
        {
            int cols = _boardData.Columns;
            int rows = _boardData.Rows;

            for (int x = 0; x < cols; x++)
            {
                int writeY = 0;
                for (int readY = 0; readY < rows; readY++)
                {
                    if (_boardData[x, readY] != (TileType)(-1))
                    {
                        if (writeY != readY)
                        {
                            phase.Add(new FallCommand
                            {
                                From = new Vector2Int(x, readY),
                                To = new Vector2Int(x, writeY),
                                Type = _boardData[x, readY]
                            });

                            _boardData[x, writeY] = _boardData[x, readY];
                            _boardData[x, readY] = (TileType)(-1);
                        }
                        writeY++;
                    }
                }
            }
        }

        private void ProcessSpawn(BoardPhase phase)
        {
            int cols = _boardData.Columns;
            int rows = _boardData.Rows;

            for (int x = 0; x < cols; x++)
            {
                int emptyCount = 0;
                for (int y = 0; y < rows; y++)
                {
                    if (_boardData[x, y] == (TileType)(-1))
                    {
                        emptyCount++;
                        var type = RandomTileType();
                        _boardData[x, y] = type;

                        phase.Add(new SpawnCommand
                        {
                            Pos = new Vector2Int(x, y),
                            Type = type,
                            SpawnRow = rows + emptyCount - 1
                        });
                    }
                }
            }
        }

        private void ProcessShuffle(BoardPhase phase)
        {
            var tiles = new List<TileType>();
            int cols = _boardData.Columns;
            int rows = _boardData.Rows;

            for (int x = 0; x < cols; x++)
                for (int y = 0; y < rows; y++)
                    tiles.Add(_boardData[x, y]);

            for (int i = tiles.Count - 1; i > 0; i--)
            {
                int j = _rng.Next(i + 1);
                (tiles[i], tiles[j]) = (tiles[j], tiles[i]);
            }

            int idx = 0;
            var newTiles = new List<(Vector2Int pos, TileType type)>();
            for (int x = 0; x < cols; x++)
            {
                for (int y = 0; y < rows; y++)
                {
                    _boardData[x, y] = tiles[idx];
                    newTiles.Add((new Vector2Int(x, y), tiles[idx]));
                    idx++;
                }
            }

            phase.Add(new ShuffleCommand { Tiles = newTiles });
        }

        private bool HasValidMoves()
        {
            int cols = _boardData.Columns;
            int rows = _boardData.Rows;

            for (int x = 0; x < cols; x++)
            {
                for (int y = 0; y < rows; y++)
                {
                    if (x + 1 < cols)
                    {
                        _boardData.Swap(new Vector2Int(x, y), new Vector2Int(x + 1, y));
                        bool hasMatch = FindAllMatches().Count > 0;
                        _boardData.Swap(new Vector2Int(x, y), new Vector2Int(x + 1, y));
                        if (hasMatch) return true;
                    }

                    if (y + 1 < rows)
                    {
                        _boardData.Swap(new Vector2Int(x, y), new Vector2Int(x, y + 1));
                        bool hasMatch = FindAllMatches().Count > 0;
                        _boardData.Swap(new Vector2Int(x, y), new Vector2Int(x, y + 1));
                        if (hasMatch) return true;
                    }
                }
            }

            return false;
        }

        #endregion

        #region 工具方法

        private TileType RandomTileType()
        {
            float totalWeight = 0f;
            var weights = _gameConfig.TileWeights;
            for (int i = 0; i < weights.Length; i++)
                totalWeight += weights[i];

            float roll = (float)_rng.NextDouble() * totalWeight;
            float cumulative = 0f;

            for (int i = 0; i < weights.Length; i++)
            {
                cumulative += weights[i];
                if (roll <= cumulative)
                    return (TileType)i;
            }

            return (TileType)(weights.Length - 1);
        }

        public TileType GetTileType(int x, int y) => _boardData[x, y];
        public (int cols, int rows) GetBoardSize() => (_gameConfig.BoardColumns, _gameConfig.BoardRows);

        public void SetProcessing(bool processing) => _isProcessing = processing;

        #endregion
    }
}
