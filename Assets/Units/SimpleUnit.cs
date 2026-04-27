using System.Collections.Generic;
using UnityEngine;

namespace TideboundWar
{
    /// <summary>
    /// 阵营标记：友军 / 敌军。攻击圈碰撞检测只对异阵营生效。
    /// </summary>
    public enum Faction { Ally, Enemy }

    /// <summary>
    /// 士兵单位状态
    /// </summary>
    public enum UnitState
    {
        /// <summary>从出生点前往最终站位</summary>
        Entering,
        /// <summary>到达最终站位，短暂停顿</summary>
        IdleReady,
        /// <summary>在站位附近小范围漫步</summary>
        Wandering,
        /// <summary>登陆中：沿路点移动（BoardingPoint → LandingPoint → 集结点）</summary>
        Landing,
        /// <summary>推进中：向 BattleTriggerArea 推进，到达后触发战斗</summary>
        Advance,
        /// <summary>战斗中（移动由外部或自身战斗逻辑控制）</summary>
        Combat,
        /// <summary>死亡</summary>
        Dead
    }

    /// <summary>
    /// 士兵单位：动画 + 血条 + 攻击圈 + 状态机
    /// 
    /// 预制体结构（父子分离，Animator 在 Body 上）：
    ///   父对象: SimpleUnit, Rigidbody2D, BoxCollider2D, CapsuleCollider2D
    ///           （SpriteRenderer=None，Animator 禁用）
    ///     └─ 子对象 "Body": SpriteRenderer + Animator（活跃）
    ///     └─ 子对象 "HPBar_Fill": SpriteRenderer（血条填充）
    ///     └─ 子对象 "HPBar_Bg":  SpriteRenderer（血条背景，可选）
    /// 
    /// 状态流程：
    ///   Entering → IdleReady → Wandering → Advance → Combat → Dead
    ///                 ↑                           │          │
    ///                 └───────────────────────────┘ (脱战)   │
    ///                 └─────────────────────────────────────┘ (脱战)
    /// 
    /// 动画参数（Animator Controller）：
    ///   Bool    IsWalking  — 走路/站立
    ///   Trigger DoAttack   — 攻击信号
    ///   Int     AttackType — 0=首击(Attack02), 1=连击(Attack01)
    ///   Trigger DoHurt     — 受伤信号
    ///   Trigger DoDeath    — 死亡信号
    ///   Bool    IsDead     — 死亡锁死
    /// 
    /// 受伤效果：身体闪白（用子对象 Body 的 SpriteRenderer + MaterialPropertyBlock）
    /// </summary>
    [ExecuteInEditMode]
    public class SimpleUnit : MonoBehaviour
    {
        // ────────────── Inspector 字段 ──────────────

        [Header("基础属性")]
        [Tooltip("最大生命值")] public float MaxHP = 100f;
        [Tooltip("当前生命值")] public float CurrentHP = 100f;
        [Tooltip("阵营标记")] public Faction Faction = Faction.Ally;

        [Header("组件引用")]
        [Tooltip("Animator 组件")] public Animator UnitAnimator;
        [Tooltip("身体 SpriteRenderer（闪白用）")] public SpriteRenderer BodyRenderer;

        [Header("战斗")]
        [Tooltip("Attack01 伤害（重击）")] public float Attack01Damage = 15f;
        [Tooltip("Attack02 伤害（首击）")] public float Attack02Damage = 10f;
        [Tooltip("攻击范围碰撞体（CapsuleCollider2D，勾 Trigger）")]
        public CapsuleCollider2D AttackRange;
        [Tooltip("身体碰撞体（BoxCollider2D，不勾 Trigger，给别人攻击圈检测用）")]
        public Collider2D BodyCollider;

        [Header("攻击位")]
        [Tooltip("最大同时围攻者数量")] public int MaxMeleeAttackers = 4;
        [Tooltip("攻击位半径（围攻站位离目标中心的距离）")] public float AttackSlotRadius = 0.8f;

        [Header("血条")]
        [Tooltip("血条背景 SpriteRenderer")] public SpriteRenderer HPBarBg;
        [Tooltip("血条填充 SpriteRenderer")] public SpriteRenderer HPBarFill;
        [Tooltip("血条宽度")] public float HPBarWidth = 1f;
        [Tooltip("血条高度")] public float HPBarHeight = 0.1f;
        [Tooltip("血条 Y 偏移")] public float HPBarYOffset = 1.2f;
        [Tooltip("血条渐显/渐隐速度（alpha/秒）")] public float HPBarFadeSpeed = 4f;
        [Tooltip("停止攻击/受伤后血条保留时间（秒），超时后渐隐")] public float HPBarCombatTimeout = 3f;

        [Header("受伤闪白")]
        [Tooltip("闪白持续时间（秒）")] public float FlashDuration = 0.15f;

        [Header("移动参数")]
        [Tooltip("通用移动速度（登陆/推进/战斗共用）")] public float MoveSpeed = 2f;
        [Tooltip("漫步速度")] public float WanderSpeed = 0.8f;
        [Tooltip("到达判定距离阈值")] public float ArrivalThreshold = 0.1f;

        [Header("漫步参数")]
        [Tooltip("漫步范围半径")] public float WanderRadius = 0.5f;
        [Tooltip("漫步到点后最短暂停")] public float WanderPauseMin = 1f;
        [Tooltip("漫步到点后最长暂停")] public float WanderPauseMax = 3f;
        [Tooltip("到达站位后停顿时长")] public float IdleReadyDuration = 0.5f;
        [Tooltip("漫步目标采样最大尝试次数")] public int WanderMaxAttempts = 20;
        [Tooltip("漫步时与其他友军最小距离")] public float WanderMinSeparation = 0.5f;
        [Tooltip("漫步/站位的坐标根节点（岛屿实例的Transform），设后所有本地坐标以此为准，岛屿移动时自动跟随")]
        public Transform WanderRoot;
        [Tooltip("是否输出漫步调试日志")] public bool DebugWander;

        // ────────────── 公开状态 ──────────────

        /// <summary>当前状态</summary>
        public UnitState State { get; private set; } = UnitState.Entering;
        /// <summary>逻辑死亡标记（不等待死亡动画结束）</summary>
        public bool IsDead { get; private set; }

        /// <summary>分配的最终站位（世界坐标）</summary>
        public Vector3 AnchorPosition { get; private set; }

        /// <summary>是否已分配站位</summary>
        public bool HasAnchor { get; private set; }

        /// <summary>所属的产兵区域（漫步约束边界）</summary>
        public PolygonCollider2D SpawnArea { get; private set; }

        // ────────────── 内部状态 ──────────────

        private float _lastHP;
        private int _attackComboIndex;      // 攻击连击计数：0=Attack02, 1=Attack01, 2=Attack01, 然后循环
        private float _pendingDamage;       // 当前攻击等待结算的伤害
        private float _flashAmount;         // 闪白强度 0~1
        private MaterialPropertyBlock _propBlock;

        // 状态机内部
        private Vector3 _wanderTargetLocal;   // 漫步目标点（WanderRoot 或 SpawnArea 局部坐标，岛屿移动时自动跟随）
        private Vector3 _anchorLocal;          // 站位本地坐标（WanderRoot 空间，岛屿移动时自动跟随）
        private bool _anchorLocalSet;           // 是否已设置本地站位
        private float _stateTimer;              // 状态计时器
        private bool _wanderTargetSet;          // 是否已设置漫步目标
        private bool _suppressArrivedEvent;     // 跳过 OnArrivedAtAnchor 事件（ExitCombat 用）

        // Landing 状态内部
        private Vector3[] _landingWaypoints;    // 登陆路点（世界坐标）
        private int _landingWaypointIndex;      // 当前路点索引
        private Vector3 _gatherAnchor;          // 最终集结点（世界坐标，到达后成为 AnchorPosition）
        private PolygonCollider2D _gatherArea;  // 集结区域（到达后成为 SpawnArea）

        /// <summary>是否已真正到达过 LandingPoint（即已从木板踏上岛屿）</summary>
        public bool HasReachedLandingPoint { get; private set; }

        /// <summary>战斗等待标记：战斗已开始但士兵还没到 LandingPoint，走完后直接进战斗</summary>
        private bool _combatPendingAfterLanding;

        /// <summary>通用路点移动完成回调（由 BeginMoveAlongPath 设置，路径走完后直接调用，不依赖 OnArrivedAtAnchor）</summary>
        private System.Action<SimpleUnit> _pathCompleteCallback;

        // Advance 状态内部
        private Vector3 _advanceTarget;             // 推进目标点（世界坐标）
        private PolygonCollider2D _advanceArea;     // 推进约束区域

        // 血条渐隐渐显
        private float _hpBarAlpha;              // 当前血条 alpha
        private float _hpBarTargetAlpha;        // 目标血条 alpha
        private float _lastCombatActivityTime = -999f; // 上次攻击/受伤的时间（-999=从未）

        // 攻击位管理（被别人攻击时：谁占了我的攻击位）
        private Dictionary<int, SimpleUnit> _occupiedSlots = new Dictionary<int, SimpleUnit>();

        // 当前攻击目标信息（我攻击别人时：我的目标和攻击位索引）
        private SimpleUnit _meleeTarget;
        private int _meleeSlotIndex = -1;

        // ────────────── 事件 ──────────────

        /// <summary>攻击命中帧事件。由 Animation Event 触发。</summary>
        public event System.Action<SimpleUnit, float> OnHit;

        /// <summary>攻击圈内是否有敌人</summary>
        public bool EnemyInRange { get; private set; }

        /// <summary>敌人进入攻击圈</summary>
        public event System.Action<SimpleUnit> OnEnemyEnterRange;
        /// <summary>敌人离开攻击圈</summary>
        public event System.Action<SimpleUnit> OnEnemyExitRange;

        /// <summary>到达最终站位事件</summary>
        public event System.Action<SimpleUnit> OnArrivedAtAnchor;

        // ────────────── Animator 参数名常量 ──────────────

        private static readonly int ParamIsWalking  = Animator.StringToHash("IsWalking");
        private static readonly int ParamDoAttack   = Animator.StringToHash("DoAttack");
        private static readonly int ParamAttackType = Animator.StringToHash("AttackType");
        private static readonly int ParamDoDeath    = Animator.StringToHash("DoDeath");
        private static readonly int ParamIsDead     = Animator.StringToHash("IsDead");

        // 缺失参数只警告一次
        private static readonly HashSet<int> _warnedMissingParams = new HashSet<int>();

        /// <summary>
        /// 检查 Animator Controller 是否包含指定参数（按 hash）。
        /// 如果不存在，只 Debug.LogWarning 一次，不会重复刷屏。
        /// </summary>
        private bool HasAnimatorParam(int paramHash)
        {
            if (UnitAnimator == null) return false;

            var paramCount = UnitAnimator.parameterCount;
            for (int i = 0; i < paramCount; i++)
            {
                if (UnitAnimator.GetParameter(i).nameHash == paramHash)
                    return true;
            }

            // 参数不存在，只警告一次
            if (_warnedMissingParams.Add(paramHash))
            {
                // 反查参数名（遍历已知常量）
                string name = "Unknown";
                if (paramHash == ParamIsWalking)  name = "IsWalking";
                else if (paramHash == ParamDoAttack)   name = "DoAttack";
                else if (paramHash == ParamAttackType) name = "AttackType";
                else if (paramHash == ParamDoDeath)    name = "DoDeath";
                else if (paramHash == ParamIsDead)     name = "IsDead";
                Debug.LogWarning($"[SimpleUnit] {gameObject.name} 的 Animator Controller 缺少参数: {name} (hash={paramHash})，请在 Animator 中补上");
            }
            return false;
        }

        // ────────────── 自动查找子对象引用 ──────────────

        private void AutoFindReferences()
        {
            if (UnitAnimator == null)
            {
                Transform bodyTf = transform.Find("Body");
                if (bodyTf != null)
                    UnitAnimator = bodyTf.GetComponent<Animator>();
                if (UnitAnimator == null)
                    UnitAnimator = GetComponentInChildren<Animator>();
            }

            if (BodyRenderer == null || BodyRenderer.sprite == null)
            {
                Transform bodyTf = transform.Find("Body");
                if (bodyTf != null)
                {
                    SpriteRenderer sr = bodyTf.GetComponent<SpriteRenderer>();
                    if (sr != null && sr.sprite != null)
                        BodyRenderer = sr;
                }
                if (BodyRenderer == null || BodyRenderer.sprite == null)
                {
                    foreach (var sr in GetComponentsInChildren<SpriteRenderer>())
                    {
                        if (sr.sprite != null)
                        {
                            BodyRenderer = sr;
                            break;
                        }
                    }
                }
            }

            if (HPBarFill == null)
            {
                Transform fillTf = transform.Find("HPBar_Fill");
                if (fillTf != null)
                    HPBarFill = fillTf.GetComponent<SpriteRenderer>();
            }

            if (HPBarBg == null)
            {
                Transform bgTf = transform.Find("HPBar_Bg");
                if (bgTf != null)
                    HPBarBg = bgTf.GetComponent<SpriteRenderer>();
            }

            if (AttackRange == null)
            {
                CapsuleCollider2D[] caps = GetComponents<CapsuleCollider2D>();
                foreach (var c in caps)
                {
                    if (c.isTrigger) { AttackRange = c; break; }
                }
            }

            if (BodyCollider == null)
            {
                BoxCollider2D[] boxes = GetComponents<BoxCollider2D>();
                foreach (var b in boxes)
                {
                    if (!b.isTrigger) { BodyCollider = b; break; }
                }
            }
        }

        // ────────────── 生命周期 ──────────────

        private void Awake()
        {
            AutoFindReferences();
        }

        private void Reset()
        {
            AutoFindReferences();
        }

        private void Start()
        {
            if (Application.isPlaying)
            {
                IsDead = false;
                CurrentHP = MaxHP;
                _lastHP = CurrentHP;
                _hpBarAlpha = 0f;
                _hpBarTargetAlpha = 0f; // 非战斗状态，血条隐藏
                UpdateHPBar();

                // ── 血条组件诊断 ──
                if (HPBarFill == null)
                    Debug.LogError($"[SimpleUnit] {gameObject.name}：HPBarFill 为空！请确认预制体有名为 HPBar_Fill 的子对象且挂了 SpriteRenderer");
                else if (HPBarFill.sprite == null)
                    Debug.LogError($"[SimpleUnit] {gameObject.name}：HPBarFill 的 SpriteRenderer 没有 Sprite，血条不会显示！");

                if (HPBarBg == null)
                    Debug.LogWarning($"[SimpleUnit] {gameObject.name}：HPBarBg 为空（可选），如需背景条请添加 HPBar_Bg 子对象");
            }
        }

        private void Update()
        {
            CurrentHP = Mathf.Clamp(CurrentHP, 0f, MaxHP);

            // ── 2D 俯视排序：Y 越大越靠后 ──
            if (BodyRenderer != null)
            {
                BodyRenderer.sortingOrder = Mathf.RoundToInt(-transform.position.y * 100);
            }

            // ── 血条始终在 Body 之上 ──
            int bodySortOrder = (BodyRenderer != null) ? BodyRenderer.sortingOrder : 0;
            if (HPBarFill != null)
                HPBarFill.sortingOrder = bodySortOrder + 1;
            if (HPBarBg != null)
                HPBarBg.sortingOrder = bodySortOrder + 1;

            // ── 闪白渐变恢复 ──
            if (_flashAmount > 0 && BodyRenderer != null)
            {
                _flashAmount -= Time.deltaTime / FlashDuration;
                if (_flashAmount <= 0)
                {
                    _flashAmount = 0;
                    BodyRenderer.SetPropertyBlock(null);
                }
                else
                {
                    if (_propBlock == null) _propBlock = new MaterialPropertyBlock();
                    BodyRenderer.GetPropertyBlock(_propBlock);
                    _propBlock.SetFloat("_FlashAmount", _flashAmount);
                    BodyRenderer.SetPropertyBlock(_propBlock);
                }
            }

            // ── HP 变化时更新血条 ──
            if (!Application.isPlaying || Mathf.Abs(CurrentHP - _lastHP) > 0.01f)
            {
                UpdateHPBar();
                _lastHP = CurrentHP;
            }

            // ── 血条跟随角色 ──
            if (HPBarFill != null)
            {
                Vector3 pos = transform.position;
                pos.y += HPBarYOffset;
                HPBarFill.transform.position = pos;
            }
            if (HPBarBg != null)
            {
                Vector3 bgPos = transform.position;
                bgPos.y += HPBarYOffset;
                HPBarBg.transform.position = bgPos;
            }

            // ── 血条渐隐渐显（战斗活动驱动） ──
            if (Application.isPlaying)
            {
                // 超过 HPBarCombatTimeout 没攻击也没受伤 → 血条渐隐
                if (_hpBarTargetAlpha > 0f && State != UnitState.Dead
                    && Time.time - _lastCombatActivityTime > HPBarCombatTimeout)
                {
                    _hpBarTargetAlpha = 0f;
                }

                if (!Mathf.Approximately(_hpBarAlpha, _hpBarTargetAlpha))
                {
                    _hpBarAlpha = Mathf.MoveTowards(_hpBarAlpha, _hpBarTargetAlpha, HPBarFadeSpeed * Time.deltaTime);
                    ApplyHPBarAlpha();
                }
            }

            // ── 状态机更新 ──
            if (Application.isPlaying && State != UnitState.Dead)
                UpdateStateMachine();
        }

        // ────────────── 状态机 ──────────────

        private void UpdateStateMachine()
        {
            switch (State)
            {
                case UnitState.Entering:
                    UpdateEntering();
                    break;

                case UnitState.IdleReady:
                    UpdateIdleReady();
                    break;

                case UnitState.Wandering:
                    UpdateWandering();
                    break;

                case UnitState.Landing:
                    UpdateLanding();
                    break;

                case UnitState.Advance:
                    UpdateAdvance();
                    break;

                case UnitState.Combat:
                    // Combat 状态的移动由外部控制（SimpleBattleTest 等）
                    // SimpleUnit 自身不驱动移动，只驱动动画
                    break;
            }
        }

        // ── Entering：从出生点走向最终站位 ──

        private void UpdateEntering()
        {
            if (!HasAnchor)
            {
                // 没有分配站位，原地待命（不应发生，但防错）
                SetWalking(false);
                return;
            }

            // 使用本地坐标转换：岛屿移动时站位目标跟着走
            Vector3 anchorWorld = GetAnchorWorldPosition();
            Vector3 toAnchor = anchorWorld - transform.position;
            toAnchor.z = 0f; // 忽略 Z 差异

            float dist = toAnchor.magnitude;
            if (dist <= ArrivalThreshold)
            {
                // 到达站位
                transform.position = anchorWorld;
                SetWalking(false);
                TransitionTo(UnitState.IdleReady);
                return;
            }

            // 移动向站位
            SetWalking(true);
            Vector3 dir = toAnchor.normalized;
            transform.position += dir * MoveSpeed * Time.deltaTime;
            UpdateFacing(dir);
        }

        // ── IdleReady：到达站位后短暂停顿 ──

        private void UpdateIdleReady()
        {
            _stateTimer -= Time.deltaTime;
            if (_stateTimer <= 0f)
            {
                TransitionTo(UnitState.Wandering);
            }
        }

        // ── Wandering：在站位附近小范围漫步 ──

        private void UpdateWandering()
        {
            if (!_wanderTargetSet)
            {
                // 选一个新的漫步目标点
                PickNewWanderTarget();
                return;
            }

            // 每帧把局部坐标目标转成当前世界坐标（岛屿移动时目标跟着走）
            Vector3 worldTarget = GetWanderTargetWorld();
            Vector3 toTarget = worldTarget - transform.position;
            toTarget.z = 0f;

            float dist = toTarget.magnitude;
            if (dist <= ArrivalThreshold)
            {
                // 到达漫步目标，停下来暂停一会儿
                SetWalking(false);
                _wanderTargetSet = false;
                _stateTimer = Random.Range(WanderPauseMin, WanderPauseMax);
                return;
            }

            // 如果还在暂停中，等计时器
            if (_stateTimer > 0f)
            {
                _stateTimer -= Time.deltaTime;
                return;
            }

            // 走向漫步目标
            SetWalking(true);
            Vector3 dir = toTarget.normalized;
            transform.position += dir * WanderSpeed * Time.deltaTime;
            UpdateFacing(dir);
        }

        /// <summary>把局部坐标漫步目标转成当前世界坐标</summary>
        private Vector3 GetWanderTargetWorld()
        {
            if (WanderRoot != null)
                return WanderRoot.TransformPoint(_wanderTargetLocal);
            if (SpawnArea != null)
                return SpawnArea.transform.TransformPoint(_wanderTargetLocal);
            // 没有 WanderRoot 和 SpawnArea 时退回直接使用（不应发生，但防错）
            return _wanderTargetLocal;
        }

        /// <summary>获取当前站位的世界坐标（WanderRoot 移动时自动更新）</summary>
        private Vector3 GetAnchorWorldPosition()
        {
            if (WanderRoot != null && _anchorLocalSet)
                return WanderRoot.TransformPoint(_anchorLocal);
            return AnchorPosition;
        }

        /// <summary>将 AnchorPosition 转换为 WanderRoot 本地坐标存储</summary>
        private void UpdateAnchorLocal()
        {
            if (WanderRoot != null)
            {
                _anchorLocal = WanderRoot.InverseTransformPoint(AnchorPosition);
                _anchorLocalSet = true;
            }
            else
            {
                _anchorLocalSet = false;
            }
        }

        // ── Landing：沿路点移动（下船 → 登岛 → 集结） ──

        private void UpdateLanding()
        {
            if (_landingWaypoints == null || _landingWaypointIndex >= _landingWaypoints.Length)
            {
                // 安全兜底：不应走到这里
                CompletePathMovement();
                return;
            }

            Vector3 target = _landingWaypoints[_landingWaypointIndex];
            Vector3 toTarget = target - transform.position;
            toTarget.z = 0f;

            float dist = toTarget.magnitude;
            if (dist <= ArrivalThreshold)
            {
                // 到达 LandingPoint（路点索引1）→ 标记已上岛
                // 路点顺序：0=BoardingPoint, 1=LandingPoint, 2+=集结点/其他
                if (_landingWaypointIndex == 1)
                    HasReachedLandingPoint = true;

                _landingWaypointIndex++;

                // 已上岛 且 战斗等待中 → 直接进战斗，不再走集结点
                if (HasReachedLandingPoint && _combatPendingAfterLanding)
                {
                    EnterCombatAfterLanding();
                    return;
                }

                if (_landingWaypointIndex >= _landingWaypoints.Length)
                {
                    CompletePathMovement();
                }
                return;
            }

            SetWalking(true);
            Vector3 dir = toTarget.normalized;
            transform.position += dir * MoveSpeed * Time.deltaTime;
            UpdateFacing(dir);
        }

        /// <summary>到达集结点，切换为待机漫步</summary>
        private void FinishLanding()
        {
            SetWalking(false);
            AnchorPosition = _gatherAnchor;
            HasAnchor = true;
            SpawnArea = _gatherArea;
            UpdateAnchorLocal();
            _combatPendingAfterLanding = false;
            TransitionTo(UnitState.IdleReady);
        }

        /// <summary>
        /// 走完木板后直接进入战斗（跳过集结点）。
        /// 由 UpdateLanding 在到达 LandingPoint 且 _combatPendingAfterLanding 时调用。
        /// </summary>
        private void EnterCombatAfterLanding()
        {
            SetWalking(false);
            if (_gatherArea != null)
                SpawnArea = _gatherArea;
            AnchorPosition = transform.position;
            HasAnchor = true;
            UpdateAnchorLocal();

            // 清除登陆数据
            _landingWaypoints = null;
            _landingWaypointIndex = 0;
            _combatPendingAfterLanding = false;

            TransitionTo(UnitState.Combat);
            Debug.Log($"[SimpleUnit] {gameObject.name} 下完木板，直接进入战斗");

            // 通知 LandingController：此士兵已"到达"（跳过了集结点）
            // LandingController 会把它移到 _landedUnits 并通过 AddAlly 加入战斗
            OnArrivedAtAnchor?.Invoke(this);
        }

        // ── Advance：向 BattleTriggerArea 推进 ──

        /// <summary>
        /// 由 LandingController 调用：开始向 BattleTriggerArea 推进。
        /// target: 推进目标点（BattleTriggerArea 内的随机点）
        /// advanceArea: 推进约束区域（BattleTriggerArea，用于到达后漫步约束）
        /// moveSpeed: 推进移动速度
        /// </summary>
        public void BeginAdvance(Vector3 target, PolygonCollider2D advanceArea)
        {
            if (State == UnitState.Dead) return;

            _advanceTarget = target;
            _advanceArea = advanceArea;

            TransitionTo(UnitState.Advance);
            Debug.Log($"[SimpleUnit] {gameObject.name} 开始推进 → {target}");
        }

        /// <summary>推进中：向目标点移动，到达后停止等待</summary>
        private void UpdateAdvance()
        {
            Vector3 toTarget = _advanceTarget - transform.position;
            toTarget.z = 0f;

            float dist = toTarget.magnitude;
            if (dist <= ArrivalThreshold)
            {
                // 到达推进目标点，停下等待
                transform.position = _advanceTarget;
                SetWalking(false);
                return;
            }

            // 继续推进
            SetWalking(true);
            Vector3 dir = toTarget.normalized;
            transform.position += dir * MoveSpeed * Time.deltaTime;
            UpdateFacing(dir);
        }

        /// <summary>
        /// 在当前位置附近选一个随机漫步目标。
        /// 约束：
        ///   1. 目标点距离当前位置不超过 WanderRadius
        ///   2. 目标点在 SpawnArea 内（OverlapPoint 验证，区域随父物体移动）
        ///   3. 目标点与其他 SimpleUnit 保持 WanderMinSeparation
        /// 如果连续 WanderMaxAttempts 次找不到合法点，原地待命。
        ///
        /// 采样用世界坐标验证，通过后转成 WanderRoot（优先）或 SpawnArea 局部坐标保存。
        /// 这样岛屿移动时，局部坐标目标跟着根节点走，不会过期。
        /// </summary>
        private void PickNewWanderTarget()
        {
            for (int attempt = 0; attempt < WanderMaxAttempts; attempt++)
            {
                // 在当前位置附近随机候选点（世界坐标）
                Vector2 offset = Random.insideUnitCircle * WanderRadius;
                Vector3 candidateWorld = new Vector3(
                    transform.position.x + offset.x,
                    transform.position.y + offset.y,
                    transform.position.z
                );

                // ── 检查 1：目标点是否在 SpawnArea 内（世界坐标验证） ──
                if (SpawnArea != null)
                {
                    Vector2 testPt = new Vector2(candidateWorld.x, candidateWorld.y);
                    if (!SpawnArea.OverlapPoint(testPt))
                        continue;
                }

                // ── 检查 2：与其他友军的最小间距 ──
                if (IsTooCloseToOtherUnit(candidateWorld))
                    continue;

                // 全部通过，转成本地坐标保存（优先 WanderRoot，退回 SpawnArea）
                if (WanderRoot != null)
                    _wanderTargetLocal = WanderRoot.InverseTransformPoint(candidateWorld);
                else if (SpawnArea != null)
                    _wanderTargetLocal = SpawnArea.transform.InverseTransformPoint(candidateWorld);
                else
                    _wanderTargetLocal = candidateWorld; // 退路

                _wanderTargetSet = true;

                // ── 调试日志 ──
                if (DebugWander)
                {
                    Debug.Log($"[SimpleUnit] {gameObject.name} 新漫步目标 world = {candidateWorld}");
                    Debug.Log($"[SimpleUnit] {gameObject.name} 新漫步目标 local = {_wanderTargetLocal}");
                    Debug.Log($"[SimpleUnit] {gameObject.name} 当前 WanderRoot = {(WanderRoot != null ? WanderRoot.name : "null")}");
                }

                return;
            }

            // 超过最大尝试次数，原地待命（不设置目标，等下次暂停后再试）
            _wanderTargetSet = false;
            _stateTimer = Random.Range(WanderPauseMin, WanderPauseMax);
        }

        /// <summary>检查候选点是否太靠近其他友军</summary>
        private bool IsTooCloseToOtherUnit(Vector3 candidate)
        {
            // 查找场景中所有 SimpleUnit
            // 注意：这每帧只在做漫步目标采样时调用，频率不高
            SimpleUnit[] allUnits = FindObjectsByType<SimpleUnit>(FindObjectsSortMode.None);
            foreach (var other in allUnits)
            {
                if (other == null || other == this || other.State == UnitState.Dead) continue;
                float dist = Vector2.Distance(candidate, other.transform.position);
                if (dist < WanderMinSeparation)
                    return true;
            }
            return false;
        }

        // ────────────── 状态切换 ──────────────

        private void TransitionTo(UnitState newState)
        {
            if (State == newState) return;

            State = newState;

            switch (newState)
            {
                case UnitState.Entering:
                    SetWalking(true);
                    break;

                case UnitState.IdleReady:
                    SetWalking(false);
                    _stateTimer = IdleReadyDuration;
                    if (_suppressArrivedEvent)
                        _suppressArrivedEvent = false;  // 只跳过一次
                    else
                        OnArrivedAtAnchor?.Invoke(this);
                    break;

                case UnitState.Wandering:
                    _wanderTargetSet = false;
                    _stateTimer = 0f; // 立刻开始选目标
                    break;

                case UnitState.Landing:
                    break;

                case UnitState.Advance:
                    SetWalking(true);
                    break;

                case UnitState.Combat:
                    SetWalking(false); // 进入战斗后，行走动画由 BattleManager 全权控制
                    break;

                case UnitState.Dead:
                    SetWalking(false);
                    _hpBarTargetAlpha = 0f;
                    break;
            }
        }

        // ────────────── 公开方法：状态控制 ──────────────

        /// <summary>
        /// 由 UnitSpawner 调用：设置最终站位、所属区域并开始入场。
        /// 单位会从当前位置（出生点）走向 anchorPos。
        /// </summary>
        public void BeginEntering(Vector3 anchorPos, PolygonCollider2D spawnArea)
        {
            if (IsDead) return;
            AnchorPosition = anchorPos;
            HasAnchor = true;
            SpawnArea = spawnArea;
            UpdateAnchorLocal();
            TransitionTo(UnitState.Entering);
        }

        /// <summary>进入战斗状态</summary>
        public void EnterCombat()
        {
            if (State == UnitState.Dead || IsDead) return;
            TransitionTo(UnitState.Combat);
        }

        /// <summary>
        /// 由 LandingController 调用：开始登陆（沿路点移动到集结点）。
        /// waypoints: 移动路线（如 BoardingPoint → LandingPoint → 集结点）
        /// gatherAnchor: 最终集结点（到达后成为 AnchorPosition）
        /// gatherArea: 集结区域（到达后成为 SpawnArea，用于漫步约束）
        /// </summary>
        public void BeginLanding(Vector3[] waypoints, Vector3 gatherAnchor, PolygonCollider2D gatherArea)
        {
            if (State == UnitState.Dead || IsDead) return;

            _landingWaypoints = waypoints;
            _landingWaypointIndex = 0;
            _gatherAnchor = gatherAnchor;
            _gatherArea = gatherArea;
            _combatPendingAfterLanding = false;

            TransitionTo(UnitState.Landing);
        }

        /// <summary>
        /// 通用路点移动：沿 path 走到最后一个点时调用 onComplete(this)。
        /// 与 BeginLanding 的区别：
        ///   - BeginLanding 走完后调 FinishLanding()，通过 OnArrivedAtAnchor 通知外部；
        ///   - BeginMoveAlongPath 走完后直接调 onComplete，不依赖状态机事件，更可靠。
        /// 登陆用 BeginLanding，回船等非登陆场景用此方法。
        /// </summary>
        /// <param name="path">路点数组（世界坐标）</param>
        /// <param name="onComplete">路径走完后的回调，传入 this</param>
        /// <param name="finalAnchor">到达后的站位（成为 AnchorPosition）</param>
        /// <param name="finalArea">到达后的漫步约束区域（成为 SpawnArea）</param>
        public void BeginMoveAlongPath(Vector3[] path, System.Action<SimpleUnit> onComplete,
            Vector3 finalAnchor, PolygonCollider2D finalArea)
        {
            if (State == UnitState.Dead || IsDead) return;

            _landingWaypoints = path;
            _landingWaypointIndex = 0;
            _gatherAnchor = finalAnchor;
            _gatherArea = finalArea;
            _pathCompleteCallback = onComplete;
            _combatPendingAfterLanding = false;

            TransitionTo(UnitState.Landing);
        }

        /// <summary>
        /// 路径移动完成处理：
        ///   - 如果有 _pathCompleteCallback（由 BeginMoveAlongPath 设置），直接调回调；
        ///   - 否则调 FinishLanding（传统登陆流程）。
        /// </summary>
        private void CompletePathMovement()
        {
            if (_pathCompleteCallback != null)
            {
                var cb = _pathCompleteCallback;
                _pathCompleteCallback = null;

                SetWalking(false);
                AnchorPosition = _gatherAnchor;
                HasAnchor = true;
                SpawnArea = _gatherArea;
                UpdateAnchorLocal();

                // 抑制 OnArrivedAtAnchor，由回调直接通知，不走状态机事件
                _suppressArrivedEvent = true;
                TransitionTo(UnitState.IdleReady);

                cb(this);
            }
            else
            {
                FinishLanding();
            }
        }

        /// <summary>
        /// 标记走完木板后直接进战斗（跳过集结点）。
        /// 由 LandingController 在战斗已开始时调用。
        /// 士兵会继续走完木板，到达 LandingPoint 后自动切 Combat。
        /// </summary>
        public void SetCombatPending()
        {
            _combatPendingAfterLanding = true;
        }

        /// <summary>
        /// 中断当前路点移动（Landing 状态），清空路径和回调，转 IdleReady。
        /// 用于回船时中断正在进行的登陆路线，让外部系统重新安排路径。
        /// 不会触发 OnArrivedAtAnchor 事件。
        /// </summary>
        public void InterruptCurrentPath()
        {
            if (State == UnitState.Dead || IsDead) return;

            // 清空路径和回调
            _landingWaypoints = null;
            _landingWaypointIndex = 0;
            _pathCompleteCallback = null;
            _combatPendingAfterLanding = false;

            SetWalking(false);
            AnchorPosition = transform.position;
            HasAnchor = true;
            UpdateAnchorLocal();

            // 抑制 OnArrivedAtAnchor，外部会通过新路径重新安排
            _suppressArrivedEvent = true;
            TransitionTo(UnitState.IdleReady);
        }

        /// <summary>
        /// 判断单位当前位置是否在指定多边形区域内。
        /// 用于判断士兵是否已经在船上等场景。
        /// </summary>
        public bool IsInsideArea(PolygonCollider2D area)
        {
            if (area == null) return false;
            return area.OverlapPoint(transform.position);
        }

        /// <summary>退出战斗，停在原地等待外部指令（如撤退、登陆等）</summary>
        public void ExitCombat()
        {
            if (State == UnitState.Dead || IsDead) return;
            // 转到 IdleReady：停在原地，不走回 AnchorPosition
            // 外部系统（ReturnToShipController 等）会随后通过 BeginLanding 安排移动
            _suppressArrivedEvent = true;  // 跳过误触发的 OnArrivedAtAnchor
            TransitionTo(UnitState.IdleReady);
            // 设一个很长的计时器，防止自动转入 Wandering
            // BeginLanding 时会重新设置状态，覆盖此计时器
            _stateTimer = 60f;
        }

        /// <summary>
        /// 重置登陆相关状态。在回船后、下次岛屿战斗前调用。
        /// 确保下次战斗时士兵必须重新经过 LandingPoint 才能参战。
        /// </summary>
        public void ResetLandingState()
        {
            HasReachedLandingPoint = false;
            _combatPendingAfterLanding = false;
        }

        /// <summary>
        /// 中断登陆路线，进入战斗状态。
        /// 逻辑：
        ///   - 已到达过 LandingPoint（HasReachedLandingPoint == true，在岛上）→ 立刻进战斗
        ///   - 还没到 LandingPoint（在船上或木板上）→ 标记 _combatPendingAfterLanding，走完木板再进战斗
        /// </summary>
        public void InterruptLandingForCombat()
        {
            if (State == UnitState.Dead || IsDead) return;

            // ── 已经在岛上（已到过 LandingPoint）→ 立刻进战斗 ──
            if (HasReachedLandingPoint)
            {
                if (_gatherArea != null)
                    SpawnArea = _gatherArea;

                AnchorPosition = transform.position;
                HasAnchor = true;
                UpdateAnchorLocal();

                _landingWaypoints = null;
                _landingWaypointIndex = 0;

                TransitionTo(UnitState.Combat);
                Debug.Log($"[SimpleUnit] {gameObject.name} 已在岛上，直接进入战斗");
                return;
            }

            // ── 还没到 LandingPoint（在船上/木板上）→ 继续走，走完木板再进战斗 ──
            _combatPendingAfterLanding = true;
            Debug.Log($"[SimpleUnit] {gameObject.name} 还没到 LandingPoint，走完木板后进入战斗");
        }

        // ────────────── 公开方法：攻击位管理 ──────────────

        /// <summary>当前攻击目标</summary>
        public SimpleUnit MeleeTarget => _meleeTarget;

        /// <summary>当前占用的攻击位索引（-1 = 无）</summary>
        public int MeleeSlotIndex => _meleeSlotIndex;

        /// <summary>是否还有空闲攻击位</summary>
        public bool HasAvailableSlot() => _occupiedSlots.Count < MaxMeleeAttackers;

        /// <summary>指定槽位是否空闲可用</summary>
        public bool CanUseSlot(int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= MaxMeleeAttackers) return false;
            return !_occupiedSlots.ContainsKey(slotIndex);
        }

        /// <summary>
        /// 为攻击者分配一个攻击位（自动选第一个空位）。
        /// 返回分配的 slotIndex（0~Max-1），-1 表示已满。
        /// 同一攻击者重复申请会返回已有位置。
        /// </summary>
        public int TryAcquireSlot(SimpleUnit attacker)
        {
            // 已占用则返回已有的 slot
            foreach (var kvp in _occupiedSlots)
            {
                if (kvp.Value == attacker) return kvp.Key;
            }
            // 找第一个空位
            for (int i = 0; i < MaxMeleeAttackers; i++)
            {
                if (!_occupiedSlots.ContainsKey(i))
                {
                    _occupiedSlots[i] = attacker;
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// 为攻击者分配指定的攻击位。成功返回 slotIndex，失败返回 -1。
        /// 如果攻击者已占用该槽位，直接返回成功。
        /// </summary>
        public int TryAcquireSpecificSlot(SimpleUnit attacker, int slotIndex)
        {
            if (slotIndex < 0 || slotIndex >= MaxMeleeAttackers) return -1;

            // 已经占用这个槽位 → 直接返回
            if (_occupiedSlots.TryGetValue(slotIndex, out SimpleUnit occupant) && occupant == attacker)
                return slotIndex;

            // 槽位已被别人占用 → 失败
            if (_occupiedSlots.ContainsKey(slotIndex))
                return -1;

            // 槽位空闲 → 占用
            _occupiedSlots[slotIndex] = attacker;
            return slotIndex;
        }

        /// <summary>释放攻击者占用的攻击位</summary>
        public void ReleaseSlot(SimpleUnit attacker)
        {
            int? toRemove = null;
            foreach (var kvp in _occupiedSlots)
            {
                if (kvp.Value == attacker) { toRemove = kvp.Key; break; }
            }
            if (toRemove.HasValue) _occupiedSlots.Remove(toRemove.Value);
        }

        /// <summary>
        /// 获取指定攻击位的世界坐标位置。
        /// 攻击位按圆形均匀分布在目标周围，每帧根据目标当前位置实时计算。
        /// 使用 Inspector 配置的 AttackSlotRadius。
        /// </summary>
        public Vector3 GetSlotWorldPosition(int slotIndex)
        {
            return GetSlotWorldPositionWithRadius(slotIndex, AttackSlotRadius);
        }

        /// <summary>
        /// 获取指定攻击位的世界坐标位置，使用自定义半径。
        /// BattleManager 用此方法根据攻击者的 AttackRange 自动调整实际站位距离，
        /// 保证站在攻击位时能打到目标，不需要继续往中心挤。
        /// </summary>
        public Vector3 GetSlotWorldPositionWithRadius(int slotIndex, float radius)
        {
            float angle = (360f / MaxMeleeAttackers) * slotIndex * Mathf.Deg2Rad;
            Vector2 offset = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * radius;
            return new Vector3(transform.position.x + offset.x, transform.position.y + offset.y, transform.position.z);
        }

        /// <summary>清空所有攻击位占用（目标死亡/战斗结束时调用）</summary>
        public void ClearAllSlots() => _occupiedSlots.Clear();

        /// <summary>获取指定攻击位的当前占用者，空则返回 null</summary>
        public SimpleUnit GetSlotOwner(int slotIndex)
        {
            _occupiedSlots.TryGetValue(slotIndex, out SimpleUnit owner);
            return owner;
        }

        /// <summary>设置当前攻击目标和攻击位</summary>
        public void SetMeleeTarget(SimpleUnit target, int slotIndex)
        {
            _meleeTarget = target;
            _meleeSlotIndex = slotIndex;
        }

        /// <summary>清除当前攻击目标引用（不释放目标上的攻击位，需由调用方手动释放）</summary>
        public void ClearMeleeTarget()
        {
            _meleeTarget = null;
            _meleeSlotIndex = -1;
        }

        // ────────────── 公开方法：动画控制 ──────────────

        /// <summary>开始走路</summary>
        public void StartWalking()
        {
            if (UnitAnimator != null && !IsDeadState() && HasAnimatorParam(ParamIsWalking))
                UnitAnimator.SetBool(ParamIsWalking, true);
        }

        /// <summary>停止走路，回到站立</summary>
        public void StopWalking()
        {
            if (UnitAnimator != null && HasAnimatorParam(ParamIsWalking))
                UnitAnimator.SetBool(ParamIsWalking, false);
        }

        private void SetWalking(bool walking)
        {
            if (walking) StartWalking(); else StopWalking();
        }

        /// <summary>朝向更新</summary>
        private void UpdateFacing(Vector3 moveDir)
        {
            if (moveDir.x != 0f)
            {
                Vector3 scale = transform.localScale;
                scale.x = Mathf.Abs(scale.x) * (moveDir.x > 0f ? 1f : -1f);
                transform.localScale = scale;
            }
        }

        /// <summary>
        /// 攻击。循环模式：Attack02 → Attack01 → Attack01 → 循环。
        /// </summary>
        public void Attack()
        {
            if (UnitAnimator == null || IsDeadState()) return;

            int attackType = (_attackComboIndex % 3 == 0) ? 0 : 1;
            _pendingDamage = (attackType == 0) ? Attack02Damage : Attack01Damage;
            if (HasAnimatorParam(ParamAttackType))
                UnitAnimator.SetInteger(ParamAttackType, attackType);
            if (HasAnimatorParam(ParamDoAttack))
                UnitAnimator.SetTrigger(ParamDoAttack);

            _attackComboIndex++;
        }

        /// <summary>动画命中帧回调</summary>
        public void OnHitFrame()
        {
            if (_pendingDamage > 0)
            {
                OnHit?.Invoke(this, _pendingDamage);
                _pendingDamage = 0;
            }
        }

        /// <summary>脱战重置连击</summary>
        public void StopAttacking()
        {
            _attackComboIndex = 0;
        }

        /// <summary>受伤</summary>
        public void TakeDamage(float damage)
        {
            if (IsDeadState()) return;

            // ── 标记战斗活动：被攻击 → 血条渐显 ──
            _lastCombatActivityTime = Time.time;
            _hpBarTargetAlpha = 1f;

            CurrentHP = Mathf.Max(0, CurrentHP - damage);
            FlashWhite();

            if (CurrentHP <= 0)
                Die();
        }

        private void FlashWhite()
        {
            if (BodyRenderer != null)
            {
                _flashAmount = 1f;
            }
        }

        /// <summary>治疗</summary>
        public void Heal(float amount)
        {
            CurrentHP = Mathf.Min(MaxHP, CurrentHP + amount);
        }

        // ────────────── 攻击圈碰撞检测 ──────────────

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (IsDead) return;
            var otherUnit = other.GetComponentInParent<SimpleUnit>();
            if (otherUnit != null && otherUnit != this && otherUnit.BodyCollider == other
                && otherUnit.Faction != this.Faction && !otherUnit.IsDead)
            {
                EnemyInRange = true;
                OnEnemyEnterRange?.Invoke(this);
            }
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            if (IsDead) return;
            var otherUnit = other.GetComponentInParent<SimpleUnit>();
            if (otherUnit != null && otherUnit != this && otherUnit.BodyCollider == other
                && otherUnit.Faction != this.Faction && !otherUnit.IsDead)
            {
                EnemyInRange = false;
                OnEnemyExitRange?.Invoke(this);
            }
        }

        // ────────────── 内部方法 ──────────────

        private void Die()
        {
            if (IsDead) return;

            IsDead = true;
            CurrentHP = 0f;
            Debug.Log($"[SimpleUnit] {gameObject.name} 逻辑死亡，立即标记 IsDead");

            StopWalking();
            StopAttacking();

            // ── 攻击位清理 ──
            // 1. 从当前目标的攻击位列表中移除自己
            if (_meleeTarget != null)
                _meleeTarget.ReleaseSlot(this);
            _meleeTarget = null;
            _meleeSlotIndex = -1;
            // 2. 清空所有攻击自己的攻击者占用的攻击位
            //    （攻击者会在下一帧 BattleManager.DriveUnits 中发现目标死亡，自动寻找新目标）
            ClearAllSlots();

            // 清除战斗等待状态
            _combatPendingAfterLanding = false;

            // 逻辑死亡时立刻禁用攻击圈，防止继续参与战斗碰撞
            if (AttackRange != null)
                AttackRange.enabled = false;

            TransitionTo(UnitState.Dead);
            if (UnitAnimator != null)
            {
                if (HasAnimatorParam(ParamIsDead))
                    UnitAnimator.SetBool(ParamIsDead, true);
                if (HasAnimatorParam(ParamDoDeath))
                    UnitAnimator.SetTrigger(ParamDoDeath);
            }
        }

        /// <summary>死亡动画结束回调（Animation Event）</summary>
        public void OnDeathEnd()
        {
            Destroy(gameObject);
        }

        private bool IsDeadState()
        {
            return IsDead ||
                   State == UnitState.Dead ||
                   (UnitAnimator != null && HasAnimatorParam(ParamIsDead) && UnitAnimator.GetBool(ParamIsDead));
        }

        // ────────────── 血条 ──────────────

        private void UpdateHPBar()
        {
            if (HPBarFill == null) return;

            float ratio = MaxHP > 0 ? CurrentHP / MaxHP : 0f;

            Vector3 scale = HPBarFill.transform.localScale;
            scale.x = HPBarWidth * ratio;
            scale.y = HPBarHeight;
            HPBarFill.transform.localScale = scale;

            if (HPBarBg != null)
            {
                Vector3 bgScale = HPBarBg.transform.localScale;
                bgScale.x = HPBarWidth;
                bgScale.y = HPBarHeight;
                HPBarBg.transform.localScale = bgScale;
            }

            // 颜色只改 RGB，alpha 由渐隐渐显系统控制
            Color fillCol = HPBarFill.color;
            fillCol.r = ratio <= 0.3f ? 1f : 0f;
            fillCol.g = ratio <= 0.3f ? 0f : 1f;
            fillCol.b = 0f;
            fillCol.a = _hpBarAlpha;
            HPBarFill.color = fillCol;

            ApplyHPBarAlpha();
        }

        /// <summary>将当前 _hpBarAlpha 应用到血条 SpriteRenderer</summary>
        private void ApplyHPBarAlpha()
        {
            if (HPBarFill != null)
            {
                Color c = HPBarFill.color;
                c.a = _hpBarAlpha;
                HPBarFill.color = c;
            }
            if (HPBarBg != null)
            {
                Color c = HPBarBg.color;
                c.a = _hpBarAlpha;
                HPBarBg.color = c;
            }
        }
    }
}
