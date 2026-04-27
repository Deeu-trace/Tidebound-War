using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 动画事件中转：Animator 在 Body 子对象上时，
    /// Animation Event 会在 Body 的 GameObject 上找方法。
    /// 这个脚本挂在 Body 上，把事件转发给父对象的 SimpleUnit。
    /// </summary>
    public class AnimEventRelay : MonoBehaviour
    {
        private SimpleUnit _unit;

        private void Awake()
        {
            _unit = GetComponentInParent<SimpleUnit>();
        }

        /// <summary>命中帧 → 转发给 SimpleUnit.OnHitFrame()</summary>
        public void OnHitFrame()
        {
            if (_unit != null) _unit.OnHitFrame();
        }

        /// <summary>死亡动画结束 → 转发给 SimpleUnit.OnDeathEnd()</summary>
        public void OnDeathEnd()
        {
            if (_unit != null) _unit.OnDeathEnd();
        }
    }
}
