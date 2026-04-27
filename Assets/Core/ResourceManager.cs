using System;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 资源管理器：保存 Gold / Wood / Stone 三种资源数量。
    ///
    /// 挂载位置：GameManager 或 Systems 空物体上。
    ///
    /// 事件：
    ///   OnResourceChanged — 任何资源数量变化后触发，ResourceUI 订阅此事件刷新显示。
    /// </summary>
    public class ResourceManager : MonoBehaviour
    {
        // ────────────── 资源数量 ──────────────

        [Header("当前资源")]
        [Tooltip("金币")] [SerializeField] private int _gold;
        [Tooltip("木材")] [SerializeField] private int _wood;
        [Tooltip("石料")] [SerializeField] private int _stone;

        // ────────────── 公开只读属性 ──────────────

        /// <summary>金币数量</summary>
        public int Gold => _gold;

        /// <summary>木材数量</summary>
        public int Wood => _wood;

        /// <summary>石料数量</summary>
        public int Stone => _stone;

        // ────────────── 事件 ──────────────

        /// <summary>资源数量变化时触发。ResourceUI 订阅此事件刷新显示。</summary>
        public event Action OnResourceChanged;

        // ────────────── 公开方法 ──────────────

        /// <summary>
        /// 增加金币。
        /// </summary>
        /// <param name="amount">增加数量（必须 > 0）</param>
        public void AddGold(int amount)
        {
            if (amount <= 0)
            {
                Debug.LogWarning("[ResourceManager] AddGold 数量必须大于 0，当前传入：" + amount);
                return;
            }

            _gold += amount;
            Debug.Log($"[ResourceManager] Gold +{amount}，当前 Gold = {_gold}");
            OnResourceChanged?.Invoke();
        }

        /// <summary>
        /// 增加木材。
        /// </summary>
        /// <param name="amount">增加数量（必须 > 0）</param>
        public void AddWood(int amount)
        {
            if (amount <= 0)
            {
                Debug.LogWarning("[ResourceManager] AddWood 数量必须大于 0，当前传入：" + amount);
                return;
            }

            _wood += amount;
            Debug.Log($"[ResourceManager] Wood +{amount}，当前 Wood = {_wood}");
            OnResourceChanged?.Invoke();
        }

        /// <summary>
        /// 增加石料。
        /// </summary>
        /// <param name="amount">增加数量（必须 > 0）</param>
        public void AddStone(int amount)
        {
            if (amount <= 0)
            {
                Debug.LogWarning("[ResourceManager] AddStone 数量必须大于 0，当前传入：" + amount);
                return;
            }

            _stone += amount;
            Debug.Log($"[ResourceManager] Stone +{amount}，当前 Stone = {_stone}");
            OnResourceChanged?.Invoke();
        }
    }
}
