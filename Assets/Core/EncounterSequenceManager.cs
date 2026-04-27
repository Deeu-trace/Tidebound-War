using System;
using System.Collections.Generic;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 遭遇序列管理器：按顺序管理岛屿遭遇流程。
    /// 
    /// 职责：
    ///   - 维护岛屿预制体列表（IslandPrefabs）
    ///   - 跟踪当前遭遇序号（CurrentIndex）
    ///   - 航行进度满后，决定下一个生成哪个岛屿
    ///   - 岛屿结束后，重置航行进度准备下一次遭遇
    /// 
    /// 不负责的：
    ///   - 不移动岛屿
    ///   - 不管理战斗
    ///   - 不管理回船
    ///   - 不处理三消结算
    /// 
    /// 挂载位置：GameManager 或 Systems 空物体上
    /// </summary>
    public class EncounterSequenceManager : MonoBehaviour
    {
        [Header("岛屿序列")]
        [Tooltip("按顺序拖入岛屿预制体：Island01、Island02、...")]
        public List<GameObject> IslandPrefabs = new List<GameObject>();

        [Header("引用")]
        [Tooltip("岛屿遭遇控制器（负责生成/移动/离开岛屿）")]
        public IslandEncounterController IslandEncounterCtrl;
        [Tooltip("航行进度管理器（负责重置进度）")]
        public VoyageProgressManager VoyageProgressMgr;

        [Header("状态（只读）")]
        [Tooltip("当前遭遇序号（0 = 第一个岛屿）")] public int CurrentIndex = 0;
        [Tooltip("序列是否已完成")] public bool IsSequenceComplete = false;
        private bool _encounterInProgress;

        /// <summary>当前岛屿遭遇完全结束后触发（岛屿已销毁）</summary>
        public event Action OnEncounterFinished;

        // ── 生命周期 ──

        private void OnEnable()
        {
            if (IslandEncounterCtrl != null)
                IslandEncounterCtrl.OnIslandEncounterFinished += OnIslandLeft;
        }

        private void OnDisable()
        {
            if (IslandEncounterCtrl != null)
                IslandEncounterCtrl.OnIslandEncounterFinished -= OnIslandLeft;
        }

        // ── 公开方法 ──

        /// <summary>
        /// 开始下一次遭遇。由 VoyageProgressManager 在航行进度满后调用。
        /// 根据 CurrentIndex 从 IslandPrefabs 中取出对应岛屿预制体，
        /// 交给 IslandEncounterController 生成和驱动。
        /// </summary>
        public void StartNextEncounter()
        {
            if (_encounterInProgress)
            {
                Debug.Log("[EncounterSequence] 当前遭遇已在进行，忽略重复触发");
                return;
            }

            if (IsSequenceComplete)
            {
                Debug.Log("[EncounterSequenceManager] 序列已完成，不再触发新遭遇");
                return;
            }

            // 检查是否还有岛屿
            if (CurrentIndex >= IslandPrefabs.Count)
            {
                Debug.Log("[EncounterSequenceManager] 没有更多岛屿，Demo 流程结束");
                IsSequenceComplete = true;
                return;
            }

            GameObject islandPrefab = IslandPrefabs[CurrentIndex];
            if (islandPrefab == null)
            {
                Debug.LogError($"[EncounterSequenceManager] IslandPrefabs[{CurrentIndex}] 为空，跳过此岛屿");
                CurrentIndex++;
                StartNextEncounter(); // 尝试下一个
                return;
            }

            Debug.Log($"[EncounterSequenceManager] 开始第 {CurrentIndex + 1} 次遭遇：{islandPrefab.name}");

            if (IslandEncounterCtrl != null)
            {
                _encounterInProgress = true;
                IslandEncounterCtrl.BeginEncounter(islandPrefab);
            }
            else
            {
                Debug.LogError("[EncounterSequenceManager] IslandEncounterController 未设置，无法开始遭遇");
            }
        }

        // ── 事件回调 ──

        /// <summary>
        /// 岛屿离开并销毁后的回调。
        /// 推进序号、重置航行进度、重新允许玩家操作三消盘。
        /// </summary>
        private void OnIslandLeft()
        {
            _encounterInProgress = false;
            Debug.Log($"[EncounterSequence] 第 {CurrentIndex + 1} 次遭遇结束");

            CurrentIndex++;
            OnEncounterFinished?.Invoke();

            // 重置航行进度，准备下一次航行
            if (VoyageProgressMgr != null)
            {
                VoyageProgressMgr.ResetVoyage();
                Debug.Log("[EncounterSequence] 遭遇结束，进入下一次航行");
            }
            else
            {
                Debug.LogWarning("[EncounterSequenceManager] VoyageProgressManager 未设置，无法重置航行进度");
            }
        }
    }
}
