using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 棋盘拖拽输入控制器。
    /// 监听 PlayerInputRouter 的 C# 事件，实现拖拽状态机。
    /// </summary>
    public class BoardInputController : MonoBehaviour
    {
        [SerializeField] private PlayerInputRouter _inputRouter;
        [SerializeField] private BoardSystem _boardSystem;
        [SerializeField] private PhaseManager _phaseManager;
        [SerializeField] private GameConfig _gameConfig;

        private enum DragState { Idle, Dragging }

        private DragState _dragState = DragState.Idle;
        private Vector2 _currentScreenPos;
        private Vector2 _pressScreenPos;
        private Vector2Int _pressGridPos;
        private bool _isAnimating;

        public bool IsAnimating
        {
            get => _isAnimating;
            set => _isAnimating = value;
        }

        public RectTransform BoardViewRect { get; set; }

        private void OnEnable()
        {
            if (_inputRouter != null)
            {
                _inputRouter.OnPressStarted += OnPressStarted;
                _inputRouter.OnPressCanceled += OnPressCanceled;
                _inputRouter.OnPoint += OnPoint;
            }
        }

        private void OnDisable()
        {
            if (_inputRouter != null)
            {
                _inputRouter.OnPressStarted -= OnPressStarted;
                _inputRouter.OnPressCanceled -= OnPressCanceled;
                _inputRouter.OnPoint -= OnPoint;
            }
        }

        private void OnPoint(Vector2 screenPos)
        {
            _currentScreenPos = screenPos;

            if (_dragState != DragState.Dragging) return;

            Vector2 delta = screenPos - _pressScreenPos;
            float threshold = _gameConfig != null ? _gameConfig.DragThreshold : 30f;

            if (delta.magnitude < threshold) return;

            Vector2Int direction;
            if (Mathf.Abs(delta.x) > Mathf.Abs(delta.y))
                direction = delta.x > 0 ? Vector2Int.right : Vector2Int.left;
            else
                direction = delta.y > 0 ? Vector2Int.up : Vector2Int.down;

            var targetPos = _pressGridPos + direction;

            if (_boardSystem != null)
            {
                var (cols, rows) = _boardSystem.GetBoardSize();
                if (targetPos.x >= 0 && targetPos.x < cols &&
                    targetPos.y >= 0 && targetPos.y < rows)
                {
                    var phases = _boardSystem.RequestSwap(_pressGridPos, targetPos);

                    if (phases != null && phases.Count > 0)
                        GameEvents.BoardAnimationRequested(phases);
                }
            }

            _dragState = DragState.Idle;
        }

        private void OnPressStarted()
        {
            if (_phaseManager != null && !_phaseManager.CanAcceptBoardInput) return;
            if (_isAnimating || (_boardSystem != null && _boardSystem.IsProcessing)) return;

            _pressScreenPos = _currentScreenPos;

            var gridPos = ScreenToGrid(_pressScreenPos);
            if (gridPos.HasValue)
            {
                _pressGridPos = gridPos.Value;
                _dragState = DragState.Dragging;
            }
        }

        private void OnPressCanceled()
        {
            _dragState = DragState.Idle;
        }

        private Vector2Int? ScreenToGrid(Vector2 screenPos)
        {
            if (_gameConfig == null) return null;

            if (BoardViewRect != null)
            {
                RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    BoardViewRect, screenPos, null, out Vector2 localPos);

                float tileSize = _gameConfig.TileSize;
                int cols = _gameConfig.BoardColumns;
                int rows = _gameConfig.BoardRows;

                float offsetX = localPos.x + cols * tileSize * 0.5f;
                float offsetY = localPos.y + rows * tileSize * 0.5f;

                int x = Mathf.FloorToInt(offsetX / tileSize);
                int y = Mathf.FloorToInt(offsetY / tileSize);

                if (x >= 0 && x < cols && y >= 0 && y < rows)
                    return new Vector2Int(x, y);

                return null;
            }

            float ts = _gameConfig.TileSize;
            int c = _gameConfig.BoardColumns;
            int r = _gameConfig.BoardRows;

            // 假设左上角为(0,0)，直接用screenPos计算
            int gx = Mathf.FloorToInt(screenPos.x / ts);
            int gy = Mathf.FloorToInt(screenPos.y / ts);

            if (gx >= 0 && gx < c && gy >= 0 && gy < r)
                return new Vector2Int(gx, gy);

            return null;
        }
    }
}
