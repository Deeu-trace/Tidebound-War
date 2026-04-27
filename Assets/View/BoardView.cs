using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;
using TMPro;

namespace TideboundWar
{
    /// <summary>
    /// 棋盘视图：消费分阶段命令(BoardPhase)，编排 DOTween Sequence 动画。
    /// 支持 [ExecuteInEditMode] 在编辑器下预览方块布局。
    /// 位置和大小均可在 Inspector 中调整。
    /// </summary>
    [ExecuteInEditMode]
    public class BoardView : MonoBehaviour
    {
        [Header("引用")]
        [SerializeField] private BoardSystem _boardSystem;
        [SerializeField] private GameConfig _gameConfig;
        [SerializeField] private BoardInputController _inputController;

        [Header("预制体")]
        [SerializeField] private GameObject _tilePrefab;

        [Header("布局设置")]
        [Tooltip("棋盘在屏幕上的锚点位置（基于父物体RectTransform的局部坐标）")]
        public Vector2 BoardPosition = new Vector2(-300f, 0f);
        [Tooltip("是否在编辑器下显示方块预览")]
        [SerializeField] private bool _showEditorPreview = true;

        private TileView[,] _tiles;
        private Sequence _currentSequence;
        private RectTransform _rectTransform;

        // 编辑器预览用的方块列表
        private List<GameObject> _previewTiles = new List<GameObject>();

        public bool IsAnimating => _currentSequence != null && _currentSequence.IsActive();

        private void Awake()
        {
            _rectTransform = GetComponent<RectTransform>();

            // DOTween 容量配置：三消下落时同时 Tween 很多，默认 200 不够
            DOTween.SetTweensCapacity(500, 100);
        }

#if UNITY_EDITOR
        private void OnEnable()
        {
            if (Application.isPlaying)
                GameEvents.OnBoardAnimationRequested += ExecutePhases;
            else
                OnValidate(); // 编辑器模式立即刷新
        }

        private void OnDisable()
        {
            if (Application.isPlaying)
                GameEvents.OnBoardAnimationRequested -= ExecutePhases;
            else
                ClearEditorPreview();
        }
#else
        private void OnEnable()
        {
            GameEvents.OnBoardAnimationRequested += ExecutePhases;
        }

        private void OnDisable()
        {
            GameEvents.OnBoardAnimationRequested -= ExecutePhases;
        }
#endif

        private void Start()
        {
            // 应用自定义位置
            if (_rectTransform != null)
                _rectTransform.anchoredPosition = BoardPosition;

            if (_inputController != null)
                _inputController.BoardViewRect = _rectTransform;

            if (_boardSystem != null)
                _boardSystem.OnBoardInitialized += OnBoardInitialized;

            ResizeBoardView();

#if !UNITY_EDITOR || UNITY_EDITOR
            if (!Application.isPlaying && _showEditorPreview)
            {
                CreateEditorPreview();
            }
            else
            {
                Invoke(nameof(TryCreateBoard), 0.1f);
            }
#endif
        }

#if UNITY_EDITOR
        protected void OnValidate()
        {
            if (_rectTransform == null)
                _rectTransform = GetComponent<RectTransform>();

            // 应用自定义位置
            if (_rectTransform != null)
                _rectTransform.anchoredPosition = BoardPosition;

            ResizeBoardView();

            // 编辑器模式下自动刷新预览
            if (!Application.isPlaying && _showEditorPreview && gameObject.activeInHierarchy)
            {
                UnityEditor.EditorApplication.delayCall += () =>
                {
                    if (this != null) CreateEditorPreview();
                };
            }
        }
#endif

        #region 编辑器预览

#if UNITY_EDITOR
        private void CreateEditorPreview()
        {
            ClearEditorPreview();
            
            if (_gameConfig == null || _tilePrefab == null) return;

            int cols = _gameConfig.BoardColumns;
            int rows = _gameConfig.BoardRows;
            float tileSize = _gameConfig.TileSize;

            var types = (TileType[])System.Enum.GetValues(typeof(TileType));

            for (int x = 0; x < cols; x++)
            {
                for (int y = 0; y < rows; y++)
                {
                    var tileObj = Instantiate(_tilePrefab, transform);
                    tileObj.name = $"Tile_{x}_{y}_Preview";
                    tileObj.hideFlags = HideFlags.DontSave;

                    var tileView = tileObj.GetComponent<TileView>();
                    TileType type = types[(x + y) % types.Length];
                    if (tileView != null)
                        tileView.Setup(new Vector2Int(x, y), type, _gameConfig);

                    var rt = tileObj.GetComponent<RectTransform>();
                    rt.anchoredPosition = GridToLocalPos(new Vector2Int(x, y));
                    rt.sizeDelta = new Vector2(tileSize - 2, tileSize - 2);

                    _previewTiles.Add(tileObj);
                }
            }
        }

        private void ClearEditorPreview()
        {
            foreach (var t in _previewTiles)
            {
                if (t != null)
                {
                    if (Application.isPlaying)
                        Destroy(t.gameObject);
                    else
                        DestroyImmediate(t.gameObject);
                }
            }
            _previewTiles.Clear();
        }
#endif

        #endregion

        #region 尺寸管理

        private void ResizeBoardView()
        {
            if (_gameConfig == null || _rectTransform == null) return;
            float w = _gameConfig.BoardColumns * _gameConfig.TileSize;
            float h = _gameConfig.BoardRows * _gameConfig.TileSize;
            _rectTransform.sizeDelta = new Vector2(w, h);
        }

        #endregion

        #region 运行时创建

        private void TryCreateBoard()
        {
            if (_tiles != null) return;
            if (_boardSystem != null && _boardSystem.BoardData != null)
                CreateTiles();
        }

        private void OnBoardInitialized(List<BoardPhase> phases)
        {
            ClearBoard();
            CreateTiles();
        }

        #endregion

        #region 创建方块

        private void CreateTiles()
        {
            if (_boardSystem == null || _gameConfig == null || _tilePrefab == null)
            {
                Debug.LogError("[BoardView] 缺少必要引用！");
                return;
            }

            int cols = _gameConfig.BoardColumns;
            int rows = _gameConfig.BoardRows;
            _tiles = new TileView[cols, rows];

            for (int x = 0; x < cols; x++)
                for (int y = 0; y < rows; y++)
                    CreateTileAt(x, y, _boardSystem.GetTileType(x, y));
        }

        private TileView CreateTileAt(int x, int y, TileType type)
        {
            var tileObj = Instantiate(_tilePrefab, transform);
            tileObj.name = $"Tile_{x}_{y}_{type}";

            var tileView = tileObj.GetComponent<TileView>();
            if (tileView == null)
            {
                Debug.LogError("[BoardView] TilePrefab 上没有 TileView！");
                Destroy(tileObj);
                return null;
            }

            var gridPos = new Vector2Int(x, y);
            tileView.Setup(gridPos, type, _gameConfig);

            var rt = tileObj.GetComponent<RectTransform>();
            rt.anchoredPosition = GridToLocalPos(gridPos);
            rt.sizeDelta = new Vector2(_gameConfig.TileSize - 2, _gameConfig.TileSize - 2);

            _tiles[x, y] = tileView;
            return tileView;
        }

        private void ClearBoard()
        {
            if (_tiles == null) return;
            foreach (var tile in _tiles)
            {
                if (tile != null)
                    Destroy(tile.gameObject);
            }
            _tiles = null;
        }

        #endregion

        #region 坐标计算

        private Vector2 GridToLocalPos(Vector2Int gridPos)
        {
            float ts = _gameConfig.TileSize;
            float bw = _gameConfig.BoardColumns * ts;
            float bh = _gameConfig.BoardRows * ts;
            return new Vector2(
                -bw * 0.5f + gridPos.x * ts + ts * 0.5f,
                -bh * 0.5f + gridPos.y * ts + ts * 0.5f
            );
        }

        #endregion

        #region 命令执行（核心）

        public void ExecutePhases(List<BoardPhase> phases)
        {
            if (phases == null || phases.Count == 0) return;

            _boardSystem?.SetProcessing(true);
            if (_inputController != null)
                _inputController.IsAnimating = true;

            _currentSequence?.Kill();
            _currentSequence = DOTween.Sequence();
            _currentSequence.SetId(TweenManager.Tags.Board);

            var tilesToDestroy = new List<TileView>();

            for (int phaseIdx = 0; phaseIdx < phases.Count; phaseIdx++)
            {
                var phase = phases[phaseIdx];
                bool isFirstCmdInPhase = true;

                foreach (var cmd in phase.Commands)
                {
                    bool useAppend = isFirstCmdInPhase;

                    switch (cmd)
                    {
                        case SwapCommand swapCmd:
                            AddSwapAnim(swapCmd, useAppend);
                            break;
                        case SwapBackCommand backCmd:
                            AddSwapBackAnim(backCmd, useAppend);
                            break;
                        case RemoveCommand removeCmd:
                            var removed = AddRemoveAnim(removeCmd, useAppend);
                            if (removed != null) tilesToDestroy.Add(removed);
                            break;
                        case FallCommand fallCmd:
                            AddFallAnim(fallCmd, useAppend);
                            break;
                        case SpawnCommand spawnCmd:
                            AddSpawnAnim(spawnCmd, useAppend);
                            break;
                        case ShuffleCommand shuffleCmd:
                            AddShuffleAnim(shuffleCmd, useAppend);
                            break;
                    }

                    isFirstCmdInPhase = false;
                }
            }

            _currentSequence.OnComplete(() =>
            {
                foreach (var tile in tilesToDestroy)
                {
                    if (tile != null && tile.gameObject != null)
                        Destroy(tile.gameObject);
                }

                _boardSystem?.SetProcessing(false);
                if (_inputController != null)
                    _inputController.IsAnimating = false;
                GameEvents.BoardAnimationComplete();
            });

            _currentSequence.Play();
        }

        #endregion

        #region 各命令的动画实现

        private void AddSwapAnim(SwapCommand cmd, bool useAppend)
        {
            var tileA = GetTile(cmd.PosA);
            var tileB = GetTile(cmd.PosB);
            if (tileA == null || tileB == null) return;

            _tiles[cmd.PosA.x, cmd.PosA.y] = tileB;
            _tiles[cmd.PosB.x, cmd.PosB.y] = tileA;
            tileA.UpdateGridPos(cmd.PosB);
            tileB.UpdateGridPos(cmd.PosA);

            float dur = _gameConfig.SwapAnimDuration;
            var tweenA = tileA.CreateMoveTween(GridToLocalPos(cmd.PosB), dur);
            var tweenB = tileB.CreateMoveTween(GridToLocalPos(cmd.PosA), dur);

            if (useAppend) _currentSequence.Append(tweenA);
            else _currentSequence.Join(tweenA);
            _currentSequence.Join(tweenB);
        }

        private void AddSwapBackAnim(SwapBackCommand cmd, bool useAppend)
        {
            var tileNowAtA = GetTile(cmd.PosA);
            var tileNowAtB = GetTile(cmd.PosB);
            if (tileNowAtA == null || tileNowAtB == null) return;

            _tiles[cmd.PosA.x, cmd.PosA.y] = tileNowAtB;
            _tiles[cmd.PosB.x, cmd.PosB.y] = tileNowAtA;
            tileNowAtB.UpdateGridPos(cmd.PosA);
            tileNowAtA.UpdateGridPos(cmd.PosB);

            float dur = _gameConfig.SwapAnimDuration;
            var tweenA = tileNowAtA.CreateMoveTween(GridToLocalPos(cmd.PosB), dur);
            var tweenB = tileNowAtB.CreateMoveTween(GridToLocalPos(cmd.PosA), dur);

            if (useAppend) _currentSequence.Append(tweenA);
            else _currentSequence.Join(tweenA);
            _currentSequence.Join(tweenB);
        }

        private TileView AddRemoveAnim(RemoveCommand cmd, bool useAppend)
        {
            var tile = GetTile(cmd.Pos);
            if (tile == null)
            {
                Debug.LogError($"[BoardView] Remove 找不到方块 ({cmd.Pos.x},{cmd.Pos.y})！");
                return null;
            }

            _tiles[cmd.Pos.x, cmd.Pos.y] = null;
            var tween = tile.CreateRemoveTween(_gameConfig.RemoveAnimDuration);

            if (useAppend) _currentSequence.Append(tween);
            else _currentSequence.Join(tween);

            return tile;
        }

        private void AddFallAnim(FallCommand cmd, bool useAppend)
        {
            var tile = GetTile(cmd.From);
            if (tile == null) tile = FindTileByGridPos(cmd.From);
            if (tile == null)
            {
                Debug.LogError($"[BoardView] Fall 找不到方块 ({cmd.From.x},{cmd.From.y})->({cmd.To.x},{cmd.To.y})");
                return;
            }

            if (_tiles[cmd.From.x, cmd.From.y] == tile)
                _tiles[cmd.From.x, cmd.From.y] = null;
            _tiles[cmd.To.x, cmd.To.y] = tile;
            tile.UpdateGridPos(cmd.To);

            int dist = Mathf.Max(1, cmd.To.y - cmd.From.y);
            float totalDur = _gameConfig.FallAnimDuration * dist;
            var tween = tile.CreateMoveTween(GridToLocalPos(cmd.To), totalDur, Ease.InQuad);

            if (useAppend) _currentSequence.Append(tween);
            else _currentSequence.Join(tween);
        }

        private void AddSpawnAnim(SpawnCommand cmd, bool useAppend)
        {
            var tileView = CreateTileAt(cmd.Pos.x, cmd.Pos.y, cmd.Type);
            if (tileView == null) return;

            var startPos = GridToLocalPos(new Vector2Int(cmd.Pos.x, cmd.SpawnRow));
            tileView.SetPositionImmediate(startPos);

            int dist = Mathf.Max(1, cmd.SpawnRow - cmd.Pos.y);
            float totalDur = _gameConfig.FallAnimDuration * dist;
            var tween = tileView.CreateMoveTween(GridToLocalPos(cmd.Pos), totalDur, Ease.InQuad);

            if (useAppend) _currentSequence.Append(tween);
            else _currentSequence.Join(tween);
        }

        private void AddShuffleAnim(ShuffleCommand cmd, bool useAppend)
        {
            float dur = _gameConfig.ShuffleAnimDuration;
            bool first = true;
            foreach (var (pos, type) in cmd.Tiles)
            {
                var tile = GetTile(pos);
                if (tile != null)
                {
                    tile.UpdateVisual(type);
                    var tween = tile.CreateShakeTween(dur);
                    if (first && useAppend)
                    {
                        _currentSequence.Append(tween);
                        first = false;
                    }
                    else
                    {
                        _currentSequence.Join(tween);
                    }
                }
            }
        }

        #endregion

        #region 工具方法

        private TileView GetTile(Vector2Int pos)
        {
            if (_tiles == null) return null;
            if (pos.x < 0 || pos.x >= _gameConfig.BoardColumns ||
                pos.y < 0 || pos.y >= _gameConfig.BoardRows)
                return null;
            return _tiles[pos.x, pos.y];
        }

        private TileView FindTileByGridPos(Vector2Int gridPos)
        {
            if (_tiles == null) return null;
            int cols = _gameConfig.BoardColumns;
            int rows = _gameConfig.BoardRows;
            for (int x = 0; x < cols; x++)
                for (int y = 0; y < rows; y++)
                {
                    var t = _tiles[x, y];
                    if (t != null && t.GridPos == gridPos) return t;
                }
            return null;
        }

        #endregion
    }
}
