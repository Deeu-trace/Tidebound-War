using UnityEngine;
using DG.Tweening;

namespace TideboundWar
{
    /// <summary>
    /// DOTween 生命周期管理工具。
    /// 负责动画的标签管理和安全播放，防止动画泄漏和被打断时的异常。
    /// 
    /// 注意：DOTween 免费版没有 SetTag，所以用 string id 作为替代方案。
    /// 通过 SetId(tag) 给 Tween 打标签，Kill 时用 DOTween.Kill(id) 按标签杀。
    /// </summary>
    public static class TweenManager
    {
        /// <summary>
        /// 动画标签常量，用于按组杀 Tween
        /// </summary>
        public static class Tags
        {
            public const string Board = "board";
            public const string Battle = "battle";
            public const string PhaseTransition = "phase_transition";
            public const string UI = "ui";
        }

        /// <summary>
        /// 按标签杀掉所有关联的 Tween
        /// </summary>
        public static void KillByTag(string tag, bool complete = false)
        {
            DOTween.Kill(tag, complete);
        }

        /// <summary>
        /// 杀掉所有 Tween（阶段大切换时用）
        /// </summary>
        public static void KillAll(bool complete = false)
        {
            DOTween.KillAll(complete);
        }

        /// <summary>
        /// 安全播放 Tween：自动处理目标被销毁的情况
        /// 使用 SetId 代替 SetTag（免费版兼容）
        /// </summary>
        public static Tweener PlaySafe(Tweener tweener, string tag = null)
        {
            if (tweener == null) return null;

            // SetLink 需要 GameObject
            if (tweener.target is GameObject go)
                tweener.SetLink(go);

            if (!string.IsNullOrEmpty(tag))
                tweener.SetId(tag);

            return tweener;
        }

        /// <summary>
        /// 安全播放 Sequence
        /// 使用 SetId 代替 SetTag（免费版兼容）
        /// </summary>
        public static Sequence PlaySafe(Sequence sequence, string tag = null)
        {
            if (sequence == null) return null;

            if (!string.IsNullOrEmpty(tag))
                sequence.SetId(tag);

            return sequence;
        }
    }
}
