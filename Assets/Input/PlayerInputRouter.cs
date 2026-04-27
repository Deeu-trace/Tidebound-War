using System;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TideboundWar
{
    /// <summary>
    /// 输入→C#事件桥接器。
    /// 只做一件事：读取 Input System 输入，转发为纯 C# 事件。
    /// 其他任何系统想接收输入，只订阅事件，不依赖 InputSystem 包。
    /// </summary>
    public class PlayerInputRouter : MonoBehaviour
    {
        // ── Gameplay 事件 ──
        /// <summary>鼠标/触屏位置更新</summary>
        public event Action<Vector2> OnPoint;
        /// <summary>按下（Press Started）</summary>
        public event Action OnPressStarted;
        /// <summary>松开（Press Canceled）</summary>
        public event Action OnPressCanceled;
        /// <summary>确认键</summary>
        public event Action OnSubmit;
        /// <summary>取消键</summary>
        public event Action OnCancel;

        // ── Debug 事件 ──
        /// <summary>洗牌棋盘 (F5)</summary>
        public event Action OnShuffleBoard;
        /// <summary>下一阶段 (F6)</summary>
        public event Action OnNextPhase;

        private GameInputActions _inputActions;

        private void Awake()
        {
            _inputActions = new GameInputActions();
            BindActions();
        }

        private void OnEnable()
        {
            _inputActions.Enable();
        }

        private void OnDisable()
        {
            _inputActions.Disable();
        }

        private void OnDestroy()
        {
            _inputActions?.Dispose();
        }

        /// <summary>
        /// 将 Input System 的回调统一绑定到 C# 事件
        /// </summary>
        private void BindActions()
        {
            var gameplay = _inputActions.Gameplay;
            gameplay.Point.performed   += ctx => OnPoint?.Invoke(ctx.ReadValue<Vector2>());
            gameplay.Point.started     += ctx => OnPoint?.Invoke(ctx.ReadValue<Vector2>());
            gameplay.Press.started     += ctx => OnPressStarted?.Invoke();
            gameplay.Press.canceled    += ctx => OnPressCanceled?.Invoke();
            gameplay.Submit.started    += ctx => OnSubmit?.Invoke();
            gameplay.Cancel.started    += ctx => OnCancel?.Invoke();

            var debug = _inputActions.Debug;
            debug.ShuffleBoard.started  += ctx => OnShuffleBoard?.Invoke();
            debug.NextPhase.started     += ctx => OnNextPhase?.Invoke();
        }
    }
}
