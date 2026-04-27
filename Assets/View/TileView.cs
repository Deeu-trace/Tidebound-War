using UnityEngine;
using UnityEngine.UI;
using DG.Tweening;
using TMPro;

namespace TideboundWar
{
    /// <summary>
    /// 单个方块的视图组件。
    /// 只负责动画播放，不包含任何游戏逻辑。
    /// 
    /// 重要架构决策：
    /// 所有动画通过 CreateXxxTween 方法创建 Tweener，但不自动播放。
    /// BoardView 的 Sequence 负责 Append/Join 这些 Tweener 来管理时序。
    /// 
    /// 之前的大Bug：PlayRemoveAnim 内部同时创建了 scaleTween 和 fadeTween，
    /// 但只有 scaleTween 被返回加入 Sequence，fadeTween 没有加入 Sequence
    /// 就自动播放了——导致所有 Remove 的 fadeOut 同时执行，
    /// 视觉上连锁消除的方块全部同时消失。
    /// 
    /// 后期替换美术：只需在 Inspector 中换 Image 的 Sprite 即可，代码不用改。
    /// </summary>
    public class TileView : MonoBehaviour
    {
        [Header("组件引用")]
        [SerializeField] private Image _tileImage;
        [SerializeField] private TextMeshProUGUI _label;

        private RectTransform _rectTransform;
        private GameConfig _gameConfig;

        /// <summary>该方块对应的棋盘坐标</summary>
        public Vector2Int GridPos { get; private set; }

        private void Awake()
        {
            _rectTransform = GetComponent<RectTransform>();
        }

        /// <summary>
        /// 初始化方块的外观
        /// </summary>
        public void Setup(Vector2Int gridPos, TileType type, GameConfig config)
        {
            GridPos = gridPos;
            _gameConfig = config;
            UpdateVisual(type);
        }

        /// <summary>
        /// 更新方块外观（颜色+文字标签）
        /// </summary>
        public void UpdateVisual(TileType type)
        {
            if (_gameConfig == null) return;

            _tileImage.color = _gameConfig.GetTileColor(type);

            if (_label != null)
            {
                _label.text = _gameConfig.GetTileLabel(type);
            }
        }

        /// <summary>
        /// 更新棋盘坐标（数据层变化后调用）
        /// </summary>
        public void UpdateGridPos(Vector2Int newPos)
        {
            GridPos = newPos;
        }

        #region 动画方法

        /// <summary>
        /// 创建移动 Tweener（不自动播放，需要加入 Sequence）
        /// </summary>
        public Tweener CreateMoveTween(Vector2 targetAnchoredPos, float duration, Ease ease = Ease.OutQuad)
        {
            return _rectTransform.DOAnchorPos(targetAnchoredPos, duration)
                .SetEase(ease)
                .Pause()
                .SetAutoKill(false);
        }

        /// <summary>
        /// 创建消除动画 Tweener（不自动播放，需要加入 Sequence）。
        /// 包含缩放+淡出两个动画，都通过 Sequence 统一管理时序。
        /// </summary>
        public Tweener CreateRemoveTween(float duration)
        {
            // 缩放 Tween 作为主 Tween（返回给 Sequence）
            var scaleTween = _rectTransform.DOScale(Vector3.zero, duration)
                .SetEase(Ease.InBack)
                .Pause()
                .SetAutoKill(false);

            // 淡出 Tween 也交给外部 Sequence 管理，但这里无法直接返回两个 Tweener
            // 解决方案：把 fadeTween 加入 scaleTween 的 OnPlay 回调
            // 这样当 Sequence 播放 scaleTween 时，fadeTween 也会同时开始
            var fadeTween = _tileImage.DOFade(0f, duration)
                .Pause()
                .SetAutoKill(false);

            scaleTween.OnPlay(() => fadeTween.Play());
            scaleTween.OnKill(() =>
            {
                if (fadeTween != null && fadeTween.IsActive())
                    fadeTween.Kill();
            });

            return scaleTween;
        }

        /// <summary>
        /// 创建出现动画 Tweener（不自动播放，需要加入 Sequence）
        /// </summary>
        public Tweener CreateSpawnTween(float duration)
        {
            _rectTransform.localScale = Vector3.zero;
            _tileImage.color = new Color(_tileImage.color.r, _tileImage.color.g, _tileImage.color.b, 0f);

            var scaleTween = _rectTransform.DOScale(Vector3.one, duration)
                .SetEase(Ease.OutBack)
                .Pause()
                .SetAutoKill(false);

            var fadeTween = _tileImage.DOFade(1f, duration * 0.6f)
                .Pause()
                .SetAutoKill(false);

            scaleTween.OnPlay(() => fadeTween.Play());
            scaleTween.OnKill(() =>
            {
                if (fadeTween != null && fadeTween.IsActive())
                    fadeTween.Kill();
            });

            return scaleTween;
        }

        /// <summary>
        /// 创建抖动动画 Tweener（不自动播放，需要加入 Sequence）
        /// </summary>
        public Tweener CreateShakeTween(float duration)
        {
            return _rectTransform.DOShakeAnchorPos(duration, 5f, 10, 90f, false, true, ShakeRandomnessMode.Harmonic)
                .Pause()
                .SetAutoKill(false);
        }

        /// <summary>
        /// 立即设置位置（无动画）
        /// </summary>
        public void SetPositionImmediate(Vector2 anchoredPos)
        {
            _rectTransform.anchoredPosition = anchoredPos;
        }

        /// <summary>
        /// 销毁当前动画（仅在棋盘重置/清空时调用）
        /// </summary>
        public void KillCurrentTween()
        {
            // 保留空方法，ClearBoard 调用兼容
        }

        #endregion
    }
}
