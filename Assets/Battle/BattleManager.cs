using System.Collections.Generic;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// NvN 战斗管理器：开战后驱动所有友军和敌军自动战斗。
    ///
    /// 攻击位系统 v3（严格执行 MaxMeleeAttackers）：
    ///   - 核心原则：攻击位就是近战单位的最终站位，站在攻击位上攻击，不往中心挤
    ///   - 严格限制：没有 slot 的单位不能攻击任何目标
    ///   - 只认 MeleeTarget：单位只能攻击自己分配的 MeleeTarget，不攻击范围内任意敌人
    ///   - 安全半径：Mathf.Min(AttackSlotRadius, attackerRange * 0.85)，保证站在攻击位能打到
    ///   - 稳定性：已有有效目标时不每帧重选，仅目标死亡时才重新寻找
    ///   - 滞后缓冲：AttackRangeBuffer 防止在攻击范围边缘反复进出
    ///   - 面向阈值：FacingThreshold 防止微小 X 位移导致左右翻转
    /// </summary>
    public class BattleManager : MonoBehaviour
    {
        [Header("战斗参数")]
        [Tooltip("攻击间隔（秒）")] public float AttackInterval = 1.2f;
        [Tooltip("攻击位到达容差（距离攻击位多近算到位，停步）")]
        public float SlotArriveDistance = 0.15f;
        [Tooltip("攻击范围缓冲（已进入攻击状态后，超出范围+此值才退出，防边缘抖动）")]
        public float AttackRangeBuffer = 0.15f;
        [Tooltip("面朝方向切换的最小 X 位移阈值（低于此值不翻转，防左右抽搐）")]
        public float FacingThreshold = 0.05f;

        [Header("安全参数")]
        [Tooltip("启用战斗调试日志")] public bool EnableBattleDebugLog;

        [Header("胜利条件")]
        [Tooltip("清剿进度管理器（胜利条件：清剿满 + 敌人全灭）")]
        public BattleClearProgressManager ClearProgressMgr;

        [Header("号角系统")]
        [Tooltip("号角管理器（判断 LastChance：我方全灭时如果有号角可用则等待援军）")]
        public BattleHornManager HornManager;

        [Header("阶段管理")]
        [Tooltip("阶段管理器（进入/退出 LastChance 时切换阶段，锁定棋盘输入）")]
        public PhaseManager PhaseMgr;

        [Header("药水系统")]
        [Tooltip("战斗药水管理器（攻击/防御药水倍率）")]
        public BattlePotionManager PotionManager;

        // ── 内部状态 ──

        private List<SimpleUnit> _allies = new List<SimpleUnit>();
        private List<SimpleUnit> _enemies = new List<SimpleUnit>();
        private Dictionary<SimpleUnit, float> _attackTimers = new Dictionary<SimpleUnit, float>();

        // 攻击模式滞后：记录哪些单位正在攻击，退出时需超过 range+buffer
        private HashSet<SimpleUnit> _unitsInAttackMode = new HashSet<SimpleUnit>();

        // 攻击位半径不匹配警告（每对 target-attacker 只警告一次）
        private HashSet<long> _warnedSlotMismatch = new HashSet<long>();

        private bool _isActive;
        private bool _battleEnded;
        private GameObject _islandInstance;

        // 胜利条件日志防刷屏
        private bool _loggedEnemiesClearedButProgressIncomplete;
        private bool _loggedProgressCompleteButEnemiesAlive;

        // 待命状态：敌人全灭但清剿进度未满，友军在岛上漫步等待新怪
        private bool _isStandby;

        // LastChance 状态：我方全灭但有号角可用，等待玩家使用号角召唤援军
        private bool _isLastChance;

        // 正在赶来的援军数量（号角召唤后、到达 LandingPoint 前）
        // 防止援军在路上时误判失败
        private int _pendingReinforcements;

        /// <summary>战斗是否正在进行</summary>
        public bool IsBattleActive => _isActive;

        /// <summary>是否处于 LastChance 状态（我方全灭但仍有号角可用，等待援军）</summary>
        public bool IsLastChance => _isLastChance;

        /// <summary>战斗结束事件（true = 友军获胜）</summary>
        public event System.Action<bool> OnBattleEnded;

        // ── 公开方法 ──

        /// <summary>
        /// 开始战斗。由 LandingController 在开战条件满足时调用。
        /// 只应传入已登陆（HasReachedLandingPoint == true）的友军和敌军。
        /// </summary>
        public void StartBattle(List<SimpleUnit> allies, List<SimpleUnit> enemies, GameObject islandInstance = null)
        {
            // ── 防御性过滤：移除未到达 LandingPoint 的友军 ──
            for (int i = allies.Count - 1; i >= 0; i--)
            {
                if (allies[i] != null && !allies[i].HasReachedLandingPoint)
                {
                    Debug.LogWarning($"[BattleManager] 士兵 {allies[i].gameObject.name} 尚未到达 LandingPoint，拒绝加入 BattleManager");
                    allies.RemoveAt(i);
                }
            }

            _allies = new List<SimpleUnit>(allies);
            _enemies = new List<SimpleUnit>(enemies);
            _attackTimers.Clear();
            _unitsInAttackMode.Clear();
            _warnedSlotMismatch.Clear();
            _battleEnded = false;
            _islandInstance = islandInstance;
            _loggedEnemiesClearedButProgressIncomplete = false;
            _loggedProgressCompleteButEnemiesAlive = false;
            _isStandby = false;
            _isLastChance = false;
            _pendingReinforcements = 0;

            // 订阅事件 + 进入战斗状态 + 清除旧攻击位状态
            foreach (var unit in _allies)
            {
                unit.ClearMeleeTarget();
                unit.OnHit += OnUnitHit;
                unit.EnterCombat();
                _attackTimers[unit] = AttackInterval;
            }
            foreach (var unit in _enemies)
            {
                unit.ClearMeleeTarget();
                unit.OnHit += OnUnitHit;
                unit.EnterCombat();
                _attackTimers[unit] = AttackInterval;
            }

            _isActive = true;
        }

        /// <summary>
        /// 战斗进行中追加一名已登陆的友军。
        /// 防御性检查：未到达 LandingPoint 的士兵不允许加入。
        /// </summary>
        public void AddAlly(SimpleUnit unit)
        {
            if (unit == null || unit.IsDead || unit.State == UnitState.Dead) return;
            if (_allies.Contains(unit)) return;

            // 防御性检查：必须已到达 LandingPoint 才能参战
            if (!unit.HasReachedLandingPoint)
            {
                Debug.LogWarning($"[BattleManager] 士兵 {unit.gameObject.name} 尚未到达 LandingPoint，拒绝加入 BattleManager");
                return;
            }

            _allies.Add(unit);
            unit.ClearMeleeTarget();
            unit.OnHit += OnUnitHit;
            unit.EnterCombat();
            _attackTimers[unit] = AttackInterval;

            // 援军到达，退出 LastChance 并恢复战斗
            if (_isLastChance)
            {
                _isLastChance = false;
                if (_pendingReinforcements > 0)
                    _pendingReinforcements--;
                // 恢复战斗阶段，解锁棋盘输入
                if (PhaseMgr != null)
                    PhaseMgr.SetPhase(GamePhase.Battle);
                Debug.Log("[BattleManager] 援军到达，退出 LastChance，恢复战斗");
            }
        }

        /// <summary>
        /// 战斗进行中追加一名敌人（波次刷怪用）。
        /// 敌人立即进入战斗状态并加入驱动列表。
        /// 如果当前处于待命状态，自动恢复战斗。
        /// </summary>
        public void AddEnemy(SimpleUnit unit)
        {
            if (unit == null || unit.IsDead || unit.State == UnitState.Dead) return;
            if (_enemies.Contains(unit)) return;

            _enemies.Add(unit);
            unit.ClearMeleeTarget();
            unit.OnHit += OnUnitHit;
            unit.EnterCombat();
            _attackTimers[unit] = AttackInterval;
            Debug.Log($"[BattleManager] 新敌人 {unit.gameObject.name} 加入战斗，当前敌人数 {_enemies.Count}");

            // 如果友军在待命，恢复战斗
            if (_isStandby)
            {
                ResumeFromStandby();
            }
        }

        /// <summary>
        /// 通知有援军正在赶来（号角召唤时调用）。
        /// 防止援军在路上时误判失败。
        /// </summary>
        /// <param name="count">正在赶来的援军数量</param>
        public void NotifyReinforcementIncoming(int count)
        {
            if (count <= 0) return;
            _pendingReinforcements += count;
            Debug.Log($"[BattleManager] {_pendingReinforcements} 个援军正在赶来");
        }

        // ── 待命/恢复 ──

        /// <summary>
        /// 敌人全灭但清剿进度未满，友军进入岛上待命/漫步状态。
        /// 清理 BattleManager 自身对单位的引用，然后让单位自行处理退出战斗和进入漫步。
        /// </summary>
        private void EnterStandby()
        {
            _isStandby = true;
            _loggedEnemiesClearedButProgressIncomplete = true;
            Debug.Log("[BattleManager] 敌人已清空，但清剿进度未满，友军进入待命");

            foreach (var unit in _allies)
            {
                if (unit == null || unit.IsDead || unit.State == UnitState.Dead) continue;

                // 清理 BattleManager 自身对该单位的驱动引用
                _unitsInAttackMode.Remove(unit);
                _attackTimers.Remove(unit);
                unit.OnHit -= OnUnitHit;

                // 让单位自行处理：停止战斗、清空目标和攻击位、更新锚点、进入漫步
                unit.EnterIslandStandby();
            }
        }

        /// <summary>
        /// 待命中有新敌人加入，友军重新进入战斗。
        /// </summary>
        private void ResumeFromStandby()
        {
            _isStandby = false;
            _loggedEnemiesClearedButProgressIncomplete = false;
            Debug.Log("[BattleManager] 新敌人出现，友军从待命恢复战斗");

            foreach (var unit in _allies)
            {
                if (unit == null || unit.IsDead || unit.State == UnitState.Dead) continue;

                // 重新进入战斗
                unit.OnHit -= OnUnitHit;   // 先取消，防止重复订阅
                unit.OnHit += OnUnitHit;
                unit.EnterCombat();
                _attackTimers[unit] = AttackInterval;
            }
        }

        // ── 主循环 ──

        private void Update()
        {
            if (!_isActive) return;

            // 清理已死亡/已销毁的单位
            _allies.RemoveAll(u => u == null || u.CurrentHP <= 0 || u.IsDead || u.State == UnitState.Dead);
            _enemies.RemoveAll(u => u == null || u.CurrentHP <= 0 || u.IsDead || u.State == UnitState.Dead);
            _unitsInAttackMode.RemoveWhere(u => u == null || u.CurrentHP <= 0 || u.IsDead || u.State == UnitState.Dead);

            // 检查战斗结束
            bool alliesAlive = HasAliveUnit(_allies);
            bool enemiesAlive = HasAliveUnit(_enemies);

            // 友军全灭 → 检查 LastChance（有号角可用则等待援军）
            if (!alliesAlive && !_battleEnded)
            {
                bool hasHornChance = (HornManager != null && HornManager.HornCount > 0);
                bool hasReinforcementIncoming = _pendingReinforcements > 0;

                if (hasHornChance || hasReinforcementIncoming)
                {
                    if (!_isLastChance)
                    {
                        _isLastChance = true;
                        Debug.Log("[BattleManager] 我方全灭，但仍有号角可用，等待援军");
                        // 进入 LastChance 阶段，锁定棋盘输入
                        if (PhaseMgr != null)
                            PhaseMgr.SetPhase(GamePhase.LastChance);
                    }
                    // LastChance 状态下不驱动单位（友军全灭，敌人无目标会自动停步）
                    return;
                }
                else
                {
                    if (_isLastChance)
                    {
                        Debug.Log("[BattleManager] LastChance 结束，无号角且无援军在途，战斗失败");
                    }
                    EndBattle(false);
                    return;
                }
            }

            // 敌人全灭
            if (!enemiesAlive)
            {
                // 检查清剿进度是否已满
                bool clearComplete = ClearProgressMgr != null && ClearProgressMgr.IsClearComplete;

                if (clearComplete)
                {
                    // 敌人全灭 + 清剿满 → 胜利
                    if (!_battleEnded)
                    {
                        Debug.Log("[BattleManager] 敌人已清空且清剿进度已满，战斗胜利");
                        EndBattle(true);
                    }
                    return;
                }
                else
                {
                    // 敌人全灭但清剿未满 → 友军进入待命/漫步
                    if (!_isStandby)
                    {
                        EnterStandby();
                    }
                    return;
                }
            }

            // 清剿满但还有敌人 → 继续战斗
            if (ClearProgressMgr != null && ClearProgressMgr.IsClearComplete && enemiesAlive)
            {
                if (!_loggedProgressCompleteButEnemiesAlive)
                {
                    _loggedProgressCompleteButEnemiesAlive = true;
                    Debug.Log("[BattleManager] 清剿进度已满，等待清空剩余敌人");
                }
            }

            // 驱动所有单位（待命状态下不驱动，友军在漫步）
            if (_isStandby) return;

            DriveUnits(_allies, _enemies);
            DriveUnits(_enemies, _allies);
        }

        // ── 战斗驱动 ──

        /// <summary>
        /// 驱动一组单位。核心流程：
        ///   0. 每帧验证 MeleeTarget/slot 有效性，无效则清理
        ///   1. 确保有有效目标 + 攻击位（仅目标死亡时才重选）
        ///   2. 被围攻时优先原地反击（不追 slot）
        ///   3. 有 slot → 只检查 MeleeTarget 是否在攻击范围内 → 攻击
        ///   4. 有 slot 但 MeleeTarget 不在范围 → 移向攻击位（安全半径），到位后停步
        ///   5. 没有 slot → 原地等待或移到稳定外圈等待点
        /// </summary>
        private void DriveUnits(List<SimpleUnit> units, List<SimpleUnit> opponents)
        {
            foreach (var unit in units)
            {
                if (!_isActive) return;
                if (unit == null || unit.CurrentHP <= 0 || unit.IsDead || unit.State == UnitState.Dead) continue;

                // ── 0. 每帧验证 MeleeTarget / slot 有效性 ──
                ValidateTargetAndSlot(unit, opponents);

                // ── 1. 确保有有效目标（仅目标死亡/无效时才重选，不每帧重选） ──
                SimpleUnit target = unit.MeleeTarget;
                bool needNewTarget = (target == null || target.IsDead || target.CurrentHP <= 0 || target.State == UnitState.Dead);

                if (needNewTarget)
                {
                    ReleaseUnitSlot(unit);
                    if (!FindBestSlot(unit, opponents, out SimpleUnit newTarget, out int newSlotIdx))
                    {
                        // 完全没有可用目标，原地等待
                        unit.StopWalking();
                        _unitsInAttackMode.Remove(unit);
                        continue;
                    }

                    // 尝试申请指定槽位，失败则自动分配任意空位
                    int acquired;
                    if (newSlotIdx >= 0)
                    {
                        acquired = newTarget.TryAcquireSpecificSlot(unit, newSlotIdx);
                        if (acquired < 0)
                            acquired = newTarget.TryAcquireSlot(unit); // 槽位被抢，兜底
                    }
                    else
                    {
                        acquired = newTarget.TryAcquireSlot(unit);
                    }
                    unit.SetMeleeTarget(newTarget, acquired);
                    target = newTarget;

                    // 检查 AttackSlotRadius 与 AttackRange 是否匹配
                    WarnSlotRadiusMismatch(newTarget, unit);
                }

                // ── 如果没有 slot，尝试在当前目标上申请（可能别人释放了） ──
                if (unit.MeleeSlotIndex < 0 && target != null)
                {
                    int slot = target.TryAcquireSlot(unit);
                    if (slot >= 0)
                    {
                        unit.SetMeleeTarget(target, slot);
                        WarnSlotRadiusMismatch(target, unit);
                    }
                }

                if (target == null) { unit.StopWalking(); continue; }

                // ── 2. 被围攻时优先原地反击 ──
                //    如果对手已经在攻击范围内，不管有没有 slot，直接停步攻击
                float effectiveRange = GetEffectiveAttackRange(unit);

                SimpleUnit nearestOpponent = FindNearest(unit, opponents);
                if (nearestOpponent != null)
                {
                    float distToNearest = Vector2.Distance(unit.transform.position, nearestOpponent.transform.position);
                    bool inAttackMode = _unitsInAttackMode.Contains(unit);
                    float attackThreshold = inAttackMode ? effectiveRange + AttackRangeBuffer : effectiveRange;

                    if (distToNearest <= attackThreshold)
                    {
                        // 对手在攻击范围内 → 停步攻击（不管有没有 slot）
                        _unitsInAttackMode.Add(unit);
                        unit.StopWalking();
                        FaceTarget(unit, nearestOpponent);

                        _attackTimers.TryGetValue(unit, out float timer);
                        timer += Time.deltaTime;
                        if (timer >= AttackInterval)
                        {
                            // 如果 MeleeTarget 不同，临时更新目标以保证 OnUnitHit 命中
                            if (unit.MeleeTarget != nearestOpponent)
                            {
                                if (EnableBattleDebugLog)
                                    Debug.Log($"[BattleManager] {unit.gameObject.name} 被围攻反击：切换目标 → {nearestOpponent.gameObject.name}");
                                ReleaseUnitSlot(unit);
                                int slot = nearestOpponent.TryAcquireSlot(unit);
                                unit.SetMeleeTarget(nearestOpponent, slot);
                            }
                            unit.Attack();
                            timer = 0f;
                        }
                        _attackTimers[unit] = timer;
                        continue;
                    }
                }

                // ── 3. 有 slot：只检查 MeleeTarget 是否在攻击范围内 ──
                if (unit.MeleeSlotIndex >= 0)
                {
                    bool inAttackMode = _unitsInAttackMode.Contains(unit);
                    float attackThreshold = inAttackMode ? effectiveRange + AttackRangeBuffer : effectiveRange;

                    float distToTarget = Vector2.Distance(unit.transform.position, target.transform.position);

                    if (distToTarget <= attackThreshold)
                    {
                        // ── MeleeTarget 在攻击范围内 → 停止移动，攻击 ──
                        _unitsInAttackMode.Add(unit);
                        unit.StopWalking();
                        FaceTarget(unit, target);

                        _attackTimers.TryGetValue(unit, out float timer);
                        timer += Time.deltaTime;
                        if (timer >= AttackInterval)
                        {
                            unit.Attack();
                            timer = 0f;
                        }
                        _attackTimers[unit] = timer;
                        continue;
                    }

                    // ── MeleeTarget 不在攻击范围 → 移向攻击位（使用安全半径） ──
                    _unitsInAttackMode.Remove(unit);

                    Vector3 slotPos = GetSafeSlotPosition(target, unit, unit.MeleeSlotIndex);
                    float distToSlot = Vector2.Distance(unit.transform.position, slotPos);

                    if (distToSlot <= SlotArriveDistance)
                    {
                        // 已到达攻击位，停步等待（不往中心挤！）
                        unit.StopWalking();
                    }
                    else
                    {
                        MoveToward(unit, slotPos);
                    }
                    continue;
                }

                // ── 4. 没有 slot：在目标外圈稳定等待，不冲向中心 ──
                _unitsInAttackMode.Remove(unit);

                Vector3 awayDir = unit.transform.position - target.transform.position;
                awayDir.z = 0f;

                // 方向保护：单位与目标重叠时，用基于 instanceID 的稳定方向，不用 Vector3.right
                if (awayDir.sqrMagnitude < 0.01f)
                {
                    float stableAngle = ((unit.GetInstanceID() % 360) * 37f % 360f) * Mathf.Deg2Rad;
                    awayDir = new Vector3(Mathf.Cos(stableAngle), Mathf.Sin(stableAngle), 0f);
                }

                float waitRadius = Mathf.Max(target.AttackSlotRadius, effectiveRange) + 0.6f;
                Vector3 waitPos = target.transform.position + awayDir.normalized * waitRadius;

                float distToWait = Vector2.Distance(unit.transform.position, waitPos);

                if (distToWait <= SlotArriveDistance)
                {
                    unit.StopWalking();
                }
                else
                {
                    MoveToward(unit, waitPos);
                }
            }
        }

        /// <summary>
        /// 每帧验证单位的 MeleeTarget 和 slot 是否仍然有效。
        /// 如果发现异常，释放并清理，下一帧重新找目标。
        /// </summary>
        private void ValidateTargetAndSlot(SimpleUnit unit, List<SimpleUnit> opponents)
        {
            SimpleUnit target = unit.MeleeTarget;

            // 无目标 → 无需验证
            if (target == null) return;

            // 目标已死亡/销毁 → 清理
            if (target.IsDead || target.CurrentHP <= 0 || target.State == UnitState.Dead)
            {
                if (EnableBattleDebugLog)
                    Debug.Log($"[BattleManager] {unit.gameObject.name} 的目标 {target.gameObject.name} 已死亡，清理");
                ReleaseUnitSlot(unit);
                unit.StopWalking();
                _unitsInAttackMode.Remove(unit);
                return;
            }

            // 目标不在对手列表中（不应出现，但安全检查） → 清理
            if (!opponents.Contains(target))
            {
                Debug.LogWarning($"[BattleManager] {unit.gameObject.name} 的目标 {target.gameObject.name} 不在对手列表中，清理");
                ReleaseUnitSlot(unit);
                unit.StopWalking();
                _unitsInAttackMode.Remove(unit);
                return;
            }

            // slot 索引无效 → 清理
            int slotIdx = unit.MeleeSlotIndex;
            if (slotIdx >= 0)
            {
                // slot 超出范围
                if (slotIdx >= target.MaxMeleeAttackers)
                {
                    Debug.LogWarning($"[BattleManager] {unit.gameObject.name} 的 slotIndex({slotIdx}) >= MaxMeleeAttackers({target.MaxMeleeAttackers})，清理");
                    ReleaseUnitSlot(unit);
                    unit.StopWalking();
                    _unitsInAttackMode.Remove(unit);
                    return;
                }

                // slot 被别人占了
                SimpleUnit slotOwner = target.GetSlotOwner(slotIdx);
                if (slotOwner != null && slotOwner != unit)
                {
                    if (EnableBattleDebugLog)
                        Debug.Log($"[BattleManager] {unit.gameObject.name} 的 slot({slotIdx}) 被 {slotOwner.gameObject.name} 占据，清理");
                    ReleaseUnitSlot(unit);
                    unit.StopWalking();
                    _unitsInAttackMode.Remove(unit);
                    return;
                }
            }
        }

        /// <summary>
        /// 命中事件处理：攻击者 OnHit → 只对 MeleeTarget 扣血。
        /// 应用攻击药水倍率（友军攻击时）和防御药水倍率（友军受伤时）。
        /// MeleeTarget 已死亡则伤害丢失（严格限制攻击位，不允许兜底攻击任意敌人）。
        /// </summary>
        private void OnUnitHit(SimpleUnit attacker, float damage)
        {
            SimpleUnit target = attacker.MeleeTarget;
            if (target != null && !target.IsDead && target.CurrentHP > 0 && target.State != UnitState.Dead)
            {
                float finalDamage = damage;

                // ── 攻击药水倍率：友军攻击时伤害 × 倍率 ──
                if (attacker.Faction == Faction.Ally && PotionManager != null)
                    finalDamage *= PotionManager.GetAttackMultiplier();

                // ── 防御药水倍率：友军受伤时伤害 × 倍率 ──
                if (target.Faction == Faction.Ally && PotionManager != null)
                    finalDamage *= PotionManager.GetDefenseMultiplier();

                target.TakeDamage(finalDamage);
                CheckAndEndBattleImmediate();
            }
            // MeleeTarget 已死亡 → 伤害丢失，下一帧会找新目标
        }

        private void CheckAndEndBattleImmediate()
        {
            if (_battleEnded || !_isActive) return;

            bool hasAliveAllies = HasAliveUnit(_allies);
            bool hasAliveEnemies = HasAliveUnit(_enemies);

            // 友军全灭 → 检查 LastChance
            if (!hasAliveAllies)
            {
                bool hasHornChance = (HornManager != null && HornManager.HornCount > 0);
                bool hasReinforcementIncoming = _pendingReinforcements > 0;

                if (hasHornChance || hasReinforcementIncoming)
                {
                    if (!_isLastChance)
                    {
                        _isLastChance = true;
                        Debug.Log("[BattleManager] 我方全灭，但仍有号角可用，等待援军");
                    }
                    return;
                }

                EndBattle(false);
                return;
            }

            // 敌人全灭
            if (!hasAliveEnemies)
            {
                bool clearComplete = ClearProgressMgr != null && ClearProgressMgr.IsClearComplete;
                if (clearComplete)
                {
                    Debug.Log("[BattleManager] 检测到最后一个敌人已逻辑死亡且清剿进度已满，立即结束战斗");
                    EndBattle(true);
                }
                else if (!_isStandby)
                {
                    // 敌人全灭但清剿未满 → 进入待命
                    EnterStandby();
                }
            }
        }

        private bool HasAliveUnit(List<SimpleUnit> units)
        {
            foreach (var unit in units)
            {
                if (unit != null && !unit.IsDead && unit.CurrentHP > 0 && unit.State != UnitState.Dead)
                    return true;
            }
            return false;
        }

        private void EndBattle(bool alliesWon)
        {
            if (_battleEnded) return;
            _battleEnded = true;
            Debug.Log("[BattleManager] EndBattle 开始，停止所有战斗驱动");

            // 1) 先停止战斗驱动，防止本帧继续 MoveToward 死亡目标
            _isActive = false;

            // 2) 清理所有目标/槽位/计时和事件，确保单位立刻停止战斗行为
            var allUnits = new List<SimpleUnit>();
            allUnits.AddRange(_allies);
            allUnits.AddRange(_enemies);

            foreach (var unit in allUnits)
            {
                if (unit == null) continue;

                _unitsInAttackMode.Remove(unit);
                unit.StopWalking();
                unit.StopAttacking();

                if (unit.MeleeTarget != null)
                    unit.MeleeTarget.ReleaseSlot(unit);
                unit.ClearMeleeTarget();
                unit.ClearAllSlots();

                unit.OnHit -= OnUnitHit;
                _attackTimers.Remove(unit);

                if (unit.State != UnitState.Dead)
                    unit.ExitCombat();
            }

            _unitsInAttackMode.Clear();

            Debug.Log($"[BattleManager] 战斗结束！{(alliesWon ? "友军" : "敌人")}获胜！");
            Debug.Log("[BattleManager] EndBattle 清理完成，触发 OnBattleEnded");

            // 3) 清理完成后再通知外部系统进入回船等流程
            OnBattleEnded?.Invoke(alliesWon);
        }

        // ── 攻击位半径安全计算 ──

        /// <summary>
        /// 计算攻击位安全半径：确保站在攻击位时能打到目标。
        /// safeRadius = Mathf.Min(target.AttackSlotRadius, attackerRange * 0.85f)
        /// 如果 AttackSlotRadius > attackerRange，自动缩小，不需要往中心挤。
        /// </summary>
        private float GetSafeSlotRadius(SimpleUnit target, SimpleUnit attacker)
        {
            float attackerRange = GetEffectiveAttackRange(attacker);
            return Mathf.Min(target.AttackSlotRadius, attackerRange * 0.85f);
        }

        /// <summary>
        /// 获取攻击位世界坐标（使用安全半径）。
        /// </summary>
        private Vector3 GetSafeSlotPosition(SimpleUnit target, SimpleUnit attacker, int slotIndex)
        {
            float safeRadius = GetSafeSlotRadius(target, attacker);
            return target.GetSlotWorldPositionWithRadius(slotIndex, safeRadius);
        }

        /// <summary>
        /// 当 AttackSlotRadius 大于攻击者的 AttackRange 时，输出一次性警告。
        /// 提示用户在 Inspector 中调整参数。
        /// </summary>
        private void WarnSlotRadiusMismatch(SimpleUnit target, SimpleUnit attacker)
        {
            float attackerRange = GetEffectiveAttackRange(attacker);
            if (target.AttackSlotRadius > attackerRange)
            {
                long key = (long)target.GetInstanceID() * 100000L + attacker.GetInstanceID();
                if (_warnedSlotMismatch.Add(key))
                {
                    Debug.LogWarning(
                        $"[BattleManager] {attacker.gameObject.name} 的 AttackRange({attackerRange:F2}) " +
                        $"< {target.gameObject.name} 的 AttackSlotRadius({target.AttackSlotRadius:F2})，" +
                        $"已自动使用安全半径 {GetSafeSlotRadius(target, attacker):F2}。" +
                        $"建议在 Inspector 中调小目标的 AttackSlotRadius 或调大攻击者的 AttackRange。");
                }
            }
        }

        // ── 目标选择 ──

        /// <summary>
        /// 为单位寻找最佳的（敌人 + 攻击位）组合。
        /// 遍历所有敌人及其空攻击位，计算每个空攻击位的世界坐标（使用安全半径），
        /// 选距离单位最近的空攻击位。这样单位会自然选择人少的一侧。
        /// 如果所有攻击位都满了，返回最近的敌人（无 slot）。
        /// </summary>
        private bool FindBestSlot(SimpleUnit from, List<SimpleUnit> candidates,
                                  out SimpleUnit bestTarget, out int bestSlotIndex)
        {
            bestTarget = null;
            bestSlotIndex = -1;
            float bestSlotDist = float.MaxValue;

            // 兜底：没有任何空攻击位时，选最近的敌人
            SimpleUnit fallbackTarget = null;
            float fallbackDist = float.MaxValue;

            foreach (var candidate in candidates)
            {
                if (candidate == null || candidate.IsDead || candidate.CurrentHP <= 0 || candidate.State == UnitState.Dead) continue;

                float distToEnemy = Vector2.Distance(from.transform.position, candidate.transform.position);

                // 记录最近的兜底敌人
                if (distToEnemy < fallbackDist)
                {
                    fallbackDist = distToEnemy;
                    fallbackTarget = candidate;
                }

                // 遍历该敌人的所有空攻击位
                for (int i = 0; i < candidate.MaxMeleeAttackers; i++)
                {
                    if (!candidate.CanUseSlot(i)) continue;

                    // 使用安全半径计算攻击位位置（保证攻击者站在位上能打到）
                    Vector3 slotPos = GetSafeSlotPosition(candidate, from, i);
                    float distToSlot = Vector2.Distance(from.transform.position, slotPos);

                    if (distToSlot < bestSlotDist)
                    {
                        bestSlotDist = distToSlot;
                        bestTarget = candidate;
                        bestSlotIndex = i;
                    }
                }
            }

            // 有空攻击位 → 返回最佳 (敌人, 槽位)
            if (bestTarget != null)
                return true;

            // 没有空攻击位 → 返回最近的敌人（slotIndex=-1，单位会外圈等待）
            if (fallbackTarget != null)
            {
                bestTarget = fallbackTarget;
                bestSlotIndex = -1;
                return true;
            }

            return false;
        }

        /// <summary>纯距离最近查找（OnUnitHit 不再使用，保留备用）</summary>
        private SimpleUnit FindNearest(SimpleUnit from, List<SimpleUnit> candidates)
        {
            SimpleUnit nearest = null;
            float minDist = float.MaxValue;
            foreach (var candidate in candidates)
            {
                if (candidate == null || candidate.IsDead || candidate.CurrentHP <= 0 || candidate.State == UnitState.Dead) continue;
                float dist = Vector2.Distance(from.transform.position, candidate.transform.position);
                if (dist < minDist)
                {
                    minDist = dist;
                    nearest = candidate;
                }
            }
            return nearest;
        }

        // ── 移动与朝向 ──

        /// <summary>
        /// 让单位走向目标位置。使用 SimpleUnit.MoveSpeed。
        /// 面朝方向带阈值，防止微小位移导致翻转。
        /// 安全检查：仅拦截 NaN/Infinity，不拦截正常远距离移动。
        /// </summary>
        private void MoveToward(SimpleUnit unit, Vector3 target)
        {
            // ── 安全检查：只拦截 NaN/Infinity，不拦截距离 ──
            if (float.IsNaN(target.x) || float.IsNaN(target.y) ||
                float.IsInfinity(target.x) || float.IsInfinity(target.y))
            {
                Debug.LogWarning($"[BattleManager] {unit.gameObject.name} 的 moveTarget 是 NaN/Infinity，清理目标并停步");
                unit.StopWalking();
                ReleaseUnitSlot(unit);
                _unitsInAttackMode.Remove(unit);
                return;
            }

            Vector3 dir = target - unit.transform.position;
            dir.z = 0f;
            float dist = dir.magnitude;

            // ── 距离上限安全检查：目标异常远时输出警告并停步 ──
            if (dist > 50f)
            {
                Debug.LogWarning($"[BattleManager] {unit.gameObject.name} 的目标距离异常({dist:F1})，清理目标并停步");
                unit.StopWalking();
                ReleaseUnitSlot(unit);
                _unitsInAttackMode.Remove(unit);
                return;
            }

            unit.StartWalking();
            dir = dir.normalized;
            unit.transform.position += dir * unit.MoveSpeed * Time.deltaTime;

            // 面朝移动方向（加阈值防抖）
            UpdateFacing(unit, dir);
        }

        /// <summary>
        /// 让单位面朝目标。带阈值，X 位移太小不翻转。
        /// </summary>
        private void FaceTarget(SimpleUnit unit, SimpleUnit target)
        {
            if (target == null) return;
            Vector3 faceDir = target.transform.position - unit.transform.position;
            faceDir.z = 0f;
            UpdateFacing(unit, faceDir);
        }

        /// <summary>
        /// 统一的朝向更新。X 分量绝对值 < FacingThreshold 时不翻转，防抽搐。
        /// </summary>
        private void UpdateFacing(SimpleUnit unit, Vector3 direction)
        {
            if (Mathf.Abs(direction.x) > FacingThreshold)
            {
                Vector3 scale = unit.transform.localScale;
                scale.x = Mathf.Abs(scale.x) * (direction.x > 0f ? 1f : -1f);
                unit.transform.localScale = scale;
            }
        }

        // ── 工具方法 ──

        /// <summary>
        /// 计算单位的实际攻击范围半径。
        /// 从 AttackRange（CapsuleCollider2D）的尺寸推导，fallback=1.0。
        /// </summary>
        private float GetEffectiveAttackRange(SimpleUnit unit)
        {
            if (unit.AttackRange != null)
                return Mathf.Max(unit.AttackRange.size.x, unit.AttackRange.size.y) * 0.5f;
            return 1f;
        }

        /// <summary>
        /// 释放单位在其目标上占用的攻击位，并清除单位的 MeleeTarget 引用。
        /// </summary>
        private void ReleaseUnitSlot(SimpleUnit unit)
        {
            if (unit.MeleeTarget != null)
                unit.MeleeTarget.ReleaseSlot(unit);
            unit.ClearMeleeTarget();
        }

        /// <summary>
        /// 战斗结束时清空所有单位的攻击位。
        /// </summary>
        private void ClearAllBattleSlots()
        {
            var allUnits = new List<SimpleUnit>();
            allUnits.AddRange(_allies);
            allUnits.AddRange(_enemies);

            foreach (var unit in allUnits)
            {
                if (unit != null && unit.CurrentHP > 0)
                {
                    if (unit.MeleeTarget != null)
                        unit.MeleeTarget.ReleaseSlot(unit);
                    unit.ClearMeleeTarget();
                    unit.ClearAllSlots();
                }
            }
        }

        // ── 战后查询 ──

        /// <summary>当前存活的友军列表（只读）</summary>
        public IReadOnlyList<SimpleUnit> AliveAllies => _allies;

        /// <summary>战斗关联的岛屿实例</summary>
        public GameObject IslandInstance => _islandInstance;

        /// <summary>
        /// 让一个友军退出战斗：取消事件订阅 + 释放攻击位 + ExitCombat。
        /// </summary>
        public void DischargeAlly(SimpleUnit unit)
        {
            if (unit == null) return;
            _unitsInAttackMode.Remove(unit);
            ReleaseUnitSlot(unit);
            unit.OnHit -= OnUnitHit;
            _attackTimers.Remove(unit);
            _allies.Remove(unit);
            unit.ExitCombat();
        }
    }
}
