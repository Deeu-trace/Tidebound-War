using System.Collections.Generic;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 敌人生成器：岛屿实例化后立刻在 EnemyStandArea 内生成若干 Orc。
    /// 生成的 Orc 复用 SimpleUnit 的状态机，自动漫步在区域内。
    ///
    /// 挂载位置：岛屿预制体上（因为需要引用岛屿的 EnemyStandArea）
    /// EnemyContainer 必须指向岛屿自身子物体 Runtime/EnemyContainer，
    /// 这样 Orc 作为岛屿子物体，会随岛屿一起移动。
    /// </summary>
    public class EnemySpawner : MonoBehaviour
    {
        [Header("预制体")]
        [Tooltip("Orc 预制体")] public GameObject OrcPrefab;

        [Header("生成区域")]
        [Tooltip("敌人站位区域（PolygonCollider2D，勾 Trigger）")]
        public PolygonCollider2D EnemyStandArea;

        [Header("生成参数")]
        [Tooltip("生成数量")] public int SpawnCount = 3;
        [Tooltip("敌人之间最小间距")] public float MinSpacing = 1.0f;
        [Tooltip("随机采样最大尝试次数")] public int MaxSampleAttempts = 50;

        [Header("组织")]
        [Tooltip("生成的敌人父容器（必须指向岛屿自身的 Runtime/EnemyContainer，这样 Orc 随岛移动）")]
        public Transform EnemyContainer;

        // 已生成的敌人列表，用于间距检查
        private readonly List<Transform> _spawnedEnemies = new List<Transform>();

        /// <summary>当前存活的敌人列表（只读，供 BattleManager 等外部查询）</summary>
        public IReadOnlyList<Transform> AliveEnemies => _spawnedEnemies;

        private void Start()
        {
            SpawnEnemies();
        }

        /// <summary>生成所有敌人（Start 自动调用，也可外部调用）</summary>
        public void SpawnEnemies()
        {
            if (OrcPrefab == null)
            {
                Debug.LogError("[EnemySpawner] OrcPrefab 未设置！");
                return;
            }

            if (EnemyStandArea == null)
            {
                Debug.LogError("[EnemySpawner] EnemyStandArea 未设置！");
                return;
            }

            if (!EnsureEnemyContainer())
                return;

            for (int i = 0; i < SpawnCount; i++)
            {
                SpawnOneEnemy(i);
            }
        }

        private void SpawnOneEnemy(int index)
        {
            // ── 在 EnemyStandArea 内采样一个合法站位 ──
            Vector3 anchorPos = SampleValidPosition();

            // 校正：确保站位在区域内
            anchorPos = ForcePositionInsidePolygon(anchorPos);

            // ── 在站位处生成 Orc ──
            GameObject orcObj = Instantiate(OrcPrefab, anchorPos, Quaternion.identity, EnemyContainer);

            orcObj.name = $"Orc_{index}";

            // 面朝左边（敌人朝向）
            Vector3 scale = orcObj.transform.localScale;
            if (scale.x > 0) scale.x = -scale.x;
            orcObj.transform.localScale = scale;

            // ── 通知 SimpleUnit 开始入场 ──
            SimpleUnit unit = orcObj.GetComponent<SimpleUnit>();
            if (unit != null)
            {
                unit.Faction = Faction.Enemy;
                unit.BeginEntering(anchorPos, EnemyStandArea);
            }
            else
            {
                Debug.LogWarning($"[EnemySpawner] {orcObj.name} 没有 SimpleUnit 组件");
            }

            _spawnedEnemies.Add(orcObj.transform);

            bool isChildOfCurrentIsland = orcObj.transform.IsChildOf(transform);
            Debug.Log($"[EnemySpawner] 生成 Orc，parent = {(orcObj.transform.parent != null ? orcObj.transform.parent.name : "null")}，isChildOfCurrentIsland = {isChildOfCurrentIsland}");
            Debug.Log($"[EnemySpawner] Orc parent = {(orcObj.transform.parent != null ? orcObj.transform.parent.name : "null")}");
            Debug.Log($"[EnemySpawner] Orc 是否属于当前岛屿 = {isChildOfCurrentIsland}");
            if (!isChildOfCurrentIsland)
                Debug.LogError("[EnemySpawner] Orc 未挂到当前岛屿实例下，岛屿移动时敌人不会跟随！");
        }

        private bool EnsureEnemyContainer()
        {
            Debug.Log($"[EnemySpawner] 当前岛屿 root = {transform.name}");

            if (EnemyContainer == null)
            {
                Transform found = transform.Find("Runtime/EnemyContainer");
                if (found != null)
                    EnemyContainer = found;
            }

            Debug.Log($"[EnemySpawner] EnemyContainer = {(EnemyContainer != null ? EnemyContainer.name : "null")}");

            if (EnemyContainer == null)
            {
                Debug.LogError("[EnemySpawner] EnemyContainer 为空，无法生成敌人");
                return false;
            }

            if (!EnemyContainer.IsChildOf(transform))
            {
                Debug.LogError("[EnemySpawner] EnemyContainer 不是当前岛屿实例的子物体，敌人不会跟随岛屿移动");
                return false;
            }

            return true;
        }

        // ── 站位采样 ──

        private Vector3 SampleValidPosition()
        {
            Bounds bounds = EnemyStandArea.bounds;

            for (int attempt = 0; attempt < MaxSampleAttempts; attempt++)
            {
                float x = Random.Range(bounds.min.x, bounds.max.x);
                float y = Random.Range(bounds.min.y, bounds.max.y);
                Vector2 candidate = new Vector2(x, y);

                if (!EnemyStandArea.OverlapPoint(candidate))
                    continue;

                if (IsTooCloseToExisting(candidate))
                    continue;

                return new Vector3(candidate.x, candidate.y, 0f);
            }

            // 采样失败，返回区域中心
            Debug.LogWarning("[EnemySpawner] 未找到满足间距的站位，使用区域中心");
            return new Vector3(bounds.center.x, bounds.center.y, 0f);
        }

        private bool IsTooCloseToExisting(Vector2 candidate)
        {
            foreach (var enemy in _spawnedEnemies)
            {
                if (enemy == null) continue;
                float dist = Vector2.Distance(candidate, enemy.position);
                if (dist < MinSpacing)
                    return true;
            }
            return false;
        }

        private Vector3 ForcePositionInsidePolygon(Vector3 pos)
        {
            Vector2 testPt = new Vector2(pos.x, pos.y);

            if (EnemyStandArea.OverlapPoint(testPt))
                return pos;

            Bounds bounds = EnemyStandArea.bounds;
            Vector2 center = new Vector2(bounds.center.x, bounds.center.y);
            Vector2 dir = (center - testPt).normalized;
            float step = 0.2f;

            for (int i = 0; i < 50; i++)
            {
                testPt += dir * step;
                if (EnemyStandArea.OverlapPoint(testPt))
                    return new Vector3(testPt.x, testPt.y, pos.z);
            }

            if (EnemyStandArea.OverlapPoint(center))
                return new Vector3(center.x, center.y, pos.z);

            Debug.LogError("[EnemySpawner] 连区域中心都不在 EnemyStandArea 内！");
            return pos;
        }
    }
}
