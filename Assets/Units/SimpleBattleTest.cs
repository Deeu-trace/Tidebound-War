using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 最简战斗测试：让两个 SimpleUnit 互相打。
    /// 
    /// 攻击圈由 SimpleUnit 上的 CircleCollider2D (Trigger) 定义，
    /// 不用代码算距离，Scene 视图里直接能看到绿圈。
    /// 
    /// 伤害由 Animation Event 驱动：命中帧触发 OnHit → 对方扣血。
    /// </summary>
    public class SimpleBattleTest : MonoBehaviour
    {
        [Header("参战单位")]
        [Tooltip("左边单位（士兵）")] public SimpleUnit LeftUnit;
        [Tooltip("右边单位（怪物）")] public SimpleUnit RightUnit;

        [Header("战斗参数")]
        [Tooltip("攻击间隔（秒）")] public float AttackInterval = 1.2f;
        [Tooltip("移动速度")] public float MoveSpeed = 2f;

        private float _leftTimer;
        private float _rightTimer;
        private bool _battleOver;

        private void OnEnable()
        {
            if (LeftUnit != null)
            {
                LeftUnit.Faction = Faction.Ally;
                LeftUnit.OnHit += OnLeftHit;
                LeftUnit.OnEnemyEnterRange += OnLeftEnemyEnter;
                LeftUnit.OnEnemyExitRange += OnLeftEnemyExit;
            }
            if (RightUnit != null)
            {
                RightUnit.Faction = Faction.Enemy;
                RightUnit.OnHit += OnRightHit;
                RightUnit.OnEnemyEnterRange += OnRightEnemyEnter;
                RightUnit.OnEnemyExitRange += OnRightEnemyExit;
            }
        }

        private void OnDisable()
        {
            if (LeftUnit != null)
            {
                LeftUnit.OnHit -= OnLeftHit;
                LeftUnit.OnEnemyEnterRange -= OnLeftEnemyEnter;
                LeftUnit.OnEnemyExitRange -= OnLeftEnemyExit;
            }
            if (RightUnit != null)
            {
                RightUnit.OnHit -= OnRightHit;
                RightUnit.OnEnemyEnterRange -= OnRightEnemyEnter;
                RightUnit.OnEnemyExitRange -= OnRightEnemyExit;
            }
        }

        // ── 命中事件 ──

        private void OnLeftHit(SimpleUnit attacker, float damage)
        {
            if (RightUnit != null && RightUnit.CurrentHP > 0)
                RightUnit.TakeDamage(damage);
        }

        private void OnRightHit(SimpleUnit attacker, float damage)
        {
            if (LeftUnit != null && LeftUnit.CurrentHP > 0)
                LeftUnit.TakeDamage(damage);
        }

        // ── 攻击圈进出事件 ──

        private void OnLeftEnemyEnter(SimpleUnit unit)
        {
            LeftUnit.StopWalking();
            _leftTimer = AttackInterval; // 进圈立刻攻击，不等间隔
        }

        private void OnLeftEnemyExit(SimpleUnit unit)
        {
            // 敌人离开了攻击圈，脱战重置
            LeftUnit.StopAttacking();
        }

        private void OnRightEnemyEnter(SimpleUnit unit)
        {
            RightUnit.StopWalking();
            _rightTimer = AttackInterval;
        }

        private void OnRightEnemyExit(SimpleUnit unit)
        {
            RightUnit.StopAttacking();
        }

        // ── 主循环 ──

        private void Update()
        {
            if (LeftUnit == null || RightUnit == null || _battleOver) return;

            bool leftDead = LeftUnit.CurrentHP <= 0;
            bool rightDead = RightUnit.CurrentHP <= 0;

            if (leftDead || rightDead)
            {
                _battleOver = true;
                if (leftDead) LeftUnit.StopAttacking();
                if (rightDead) RightUnit.StopAttacking();
                Debug.Log($"[SimpleBattleTest] 战斗结束！{(leftDead ? RightUnit.gameObject.name : LeftUnit.gameObject.name)} 获胜！");
                return;
            }

            // 不在攻击圈内的单位继续走向对方
            if (!LeftUnit.EnemyInRange)
                MoveToward(LeftUnit, RightUnit.transform.position);

            if (!RightUnit.EnemyInRange)
                MoveToward(RightUnit, LeftUnit.transform.position);

            // 在攻击圈内 → 按间隔攻击
            if (LeftUnit.EnemyInRange)
            {
                _leftTimer += Time.deltaTime;
                if (_leftTimer >= AttackInterval)
                {
                    LeftUnit.Attack();
                    _leftTimer = 0f;
                }
            }

            if (RightUnit.EnemyInRange)
            {
                _rightTimer += Time.deltaTime;
                if (_rightTimer >= AttackInterval)
                {
                    RightUnit.Attack();
                    _rightTimer = 0f;
                }
            }
        }

        private void MoveToward(SimpleUnit unit, Vector3 target)
        {
            unit.StartWalking();
            Vector3 dir = (target - unit.transform.position).normalized;
            dir.y = 0;
            unit.transform.position += dir * MoveSpeed * Time.deltaTime;

            if (dir.x != 0)
            {
                Vector3 scale = unit.transform.localScale;
                scale.x = Mathf.Abs(scale.x) * (dir.x > 0 ? 1 : -1);
                unit.transform.localScale = scale;
            }
        }
    }
}
