using System;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 木板动画控制器：管理 ship_board 的伸出/收回动画。
    ///
    /// 挂载位置：ship_board 上（和 Animator 同一 GameObject）
    ///
    /// Animator Controller 需要 4 个状态：
    ///   - Board_Hidden（默认）：木板收回/隐藏静止，一帧静止动画
    ///   - Board_Extend：木板伸出动画
    ///   - Board_Extended：木板伸出后静止，一帧静止动画
    ///   - Board_Retract：木板收回动画
    ///
    /// Trigger 参数：
    ///   - DoExtend
    ///   - DoRetract
    ///
    /// 过渡：
    ///   - Board_Hidden → Board_Extend：条件 DoExtend，Has Exit Time ❌
    ///   - Board_Extend → Board_Extended：Has Exit Time ✅，Exit Time = 1
    ///   - Board_Extended → Board_Retract：条件 DoRetract，Has Exit Time ❌
    ///   - Board_Retract → Board_Hidden：Has Exit Time ✅，Exit Time = 1
    ///
    /// Animation Events（在动画剪辑末尾添加）：
    ///   - Board_Extend 剪辑末尾 → 函数名 "OnExtendFinished"
    ///   - Board_Retract 剪辑末尾 → 函数名 "OnRetractFinished"
    /// </summary>
    public class ShipBoardAnimatorController : MonoBehaviour
    {
        // ── Animator 参数名 ──
        private static readonly int ParamDoExtend  = Animator.StringToHash("DoExtend");
        private static readonly int ParamDoRetract = Animator.StringToHash("DoRetract");

        // ── 内部状态 ──
        private Animator _animator;
        private Action _onComplete;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
        }

        // ── 公开方法 ──

        /// <summary>
        /// 播放木板伸出动画。
        /// onComplete：伸出动画播放完成后回调（可为 null）。
        /// </summary>
        public void PlayExtend(Action onComplete = null)
        {
            if (_animator == null)
            {
                Debug.LogWarning("[ShipBoard] Animator 未找到，跳过伸出动画");
                onComplete?.Invoke();
                return;
            }

            _onComplete = onComplete;
            _animator.SetTrigger(ParamDoExtend);
            Debug.Log("[ShipBoard] 播放伸出动画");
        }

        /// <summary>
        /// 播放木板收回动画。
        /// onComplete：收回动画播放完成后回调（可为 null）。
        /// </summary>
        public void PlayRetract(Action onComplete = null)
        {
            if (_animator == null)
            {
                Debug.LogWarning("[ShipBoard] Animator 未找到，跳过收回动画");
                onComplete?.Invoke();
                return;
            }

            _onComplete = onComplete;
            _animator.SetTrigger(ParamDoRetract);
            Debug.Log("[ShipBoard] 播放收回动画");
        }

        // ── Animation Event 回调（由动画剪辑末尾的事件调用） ──

        /// <summary>
        /// Board_Extend 剪辑末尾的 Animation Event 调用此方法。
        /// </summary>
        public void OnExtendFinished()
        {
            Debug.Log("[ShipBoard] 伸出动画播放完成");
            FireComplete();
        }

        /// <summary>
        /// Board_Retract 剪辑末尾的 Animation Event 调用此方法。
        /// </summary>
        public void OnRetractFinished()
        {
            Debug.Log("[ShipBoard] 收回动画播放完成");
            FireComplete();
        }

        private void FireComplete()
        {
            var cb = _onComplete;
            _onComplete = null;
            cb?.Invoke();
        }
    }
}
