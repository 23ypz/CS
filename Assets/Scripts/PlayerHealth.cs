using UnityEngine;

/* 玩家生命、受伤及死亡复活状态。 */
public sealed class PlayerHealth : MonoBehaviour
{
    [Min(1f)] public float maxHealth = 100f;
    [Min(0.1f)] public float respawnDuration = 2f;
    [Header("随机复活")]
    [Min(0f)] public float respawnRadiusMin = 6f;
    [Min(0.1f)] public float respawnRadiusMax = 18f;
    [Min(1)] public int respawnAttempts = 24;
    [Tooltip("用于寻找地面的层；留空时会自动使用 Ground 层或场景碰撞体。")]
    public LayerMask respawnGroundMask;

    public float CurrentHealth { get; private set; }
    public bool IsDead { get; private set; }
    public float RespawnProgress
    {
        get
        {
            if (!IsDead) return 0f;
            return Mathf.Clamp01(1f - respawnRemaining / Mathf.Max(0.01f, respawnDuration));
        }
    }

    private float respawnRemaining; // 复活剩余秒数
    private Vector3 deathPosition; // 死亡时固定位置
    private Camera deathCamera;
    private Vector3 cameraLocalPosition;
    private Quaternion cameraLocalRotation;
    private Rigidbody body;
    private Animator animator;
    private PlayerControl controller;
    private WeaponControl weapon;
    private RecoilControl recoil;
    private Collider playerCollider;
    private float rootToGroundOffset;
    private bool serverAuthoritative; // 联机生命状态由服务器决定
    private bool originalBodyKinematic;
    private bool originalUseGravity;

    private bool UsesServerAuthority
    {
        get
        {
            NetworkClient client = NetworkClient.Active;
            return serverAuthoritative || (client != null && client.IsGameStarted);
        }
    }

    private void Awake()
    {
        maxHealth = Mathf.Max(1f, maxHealth);
        CurrentHealth = maxHealth;
        body = GetComponent<Rigidbody>();
        if (body != null)
        {
            originalBodyKinematic = body.isKinematic;
            originalUseGravity = body.useGravity;
        }
        animator = GetComponentInChildren<Animator>(true);
        controller = GetComponent<PlayerControl>();
        weapon = GetComponent<WeaponControl>();
        recoil = GetComponent<RecoilControl>();
        playerCollider = GetComponent<Collider>();
        if (playerCollider != null)
        {
            // 保持根节点到脚底的偏移，避免斜坡上脚部下沉。
            rootToGroundOffset = transform.position.y - playerCollider.bounds.min.y;
            if (rootToGroundOffset < 0f) rootToGroundOffset = 0f;
        }
        deathCamera = GetComponentInChildren<Camera>(true);
        if (deathCamera == null) deathCamera = Camera.main;
        if (deathCamera != null)
        {
            cameraLocalPosition = deathCamera.transform.localPosition;
            cameraLocalRotation = deathCamera.transform.localRotation;
        }
    }

    public void TakeDamage(float amount)
    {
        if (UsesServerAuthority || IsDead || amount <= 0f) return;
        CurrentHealth = Mathf.Max(0f, CurrentHealth - amount);
        if (CurrentHealth <= 0f) Die();
    }

    /* 应用多人服务器权威状态。 */
    public void ApplyAuthoritativeState(int health, int maximum, bool dead, float remaining)
    {
        ApplyAuthoritativeState(health, maximum, dead, remaining, transform.position);
    }

    /* 位置由服务器提供，包含复活位置。 */
    public void ApplyAuthoritativeState(int health, int maximum, bool dead, float remaining,
        Vector3 authoritativePosition)
    {
        serverAuthoritative = true;
        maxHealth = Mathf.Max(1f, maximum);
        CurrentHealth = Mathf.Clamp(health, 0, Mathf.RoundToInt(maxHealth));
        if (dead)
        {
            if (!IsDead)
            {
                transform.position = authoritativePosition;
                if (body != null) body.position = authoritativePosition;
                Die();
            }
            respawnRemaining = Mathf.Clamp(remaining, 0f, Mathf.Max(0.1f, respawnDuration));
            return;
        }
        if (IsDead)
            RestoreAliveState(authoritativePosition, true, false);
    }

    /* 清理上一局状态，不重新选择出生点。 */
    public void ResetForNewMatch()
    {
        NetworkClient client = NetworkClient.Active;
        serverAuthoritative = client != null && client.IsGameStarted;
        maxHealth = Mathf.Max(1f, maxHealth);
        CurrentHealth = maxHealth;
        RestoreAliveState(transform.position, serverAuthoritative, true);
        // 是否启用输入由模式管理器决定。
        if (controller != null) controller.enabled = true;
    }

    private void Die()
    {
        // 固定死亡位置，冻结角色、武器、动画和镜头初始姿态。
        if (IsDead) return;
        IsDead = true;
        respawnRemaining = Mathf.Max(0.1f, respawnDuration);
        deathPosition = transform.position;
        if (body != null)
        {
            if (!body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            body.isKinematic = true;
        }
        if (weapon != null)
        {
            weapon.CancelActions();
            weapon.enabled = false;
        }
        if (recoil != null) recoil.enabled = false;
        if (animator != null) animator.speed = 0f;
        if (deathCamera != null)
        {
            cameraLocalPosition = deathCamera.transform.localPosition;
            cameraLocalRotation = deathCamera.transform.localRotation;
        }
    }

    private void Update()
    {
        // 死亡期间保持位置，并逐步放倒镜头。
        if (!IsDead) return;
        transform.position = deathPosition;
        // 多人只显示倒计时，复活必须等待服务器快照。
        float deltaTime = UsesServerAuthority ? Time.unscaledDeltaTime : Time.deltaTime;
        respawnRemaining = Mathf.Max(0f, respawnRemaining - deltaTime);
        if (deathCamera != null)
        {
            float t = RespawnProgress;
            // 两秒内逐渐下俯镜头，并固定在死亡位置。
            deathCamera.transform.localPosition = cameraLocalPosition + Vector3.down * (0.25f * t);
            deathCamera.transform.localRotation = cameraLocalRotation *
                Quaternion.Euler(72f * t, 0f, 0f);
        }
        if (respawnRemaining <= 0f && !UsesServerAuthority) Respawn();
    }

    private void Respawn()
    {
        // 在死亡点附近寻找安全位置，失败时才回退原点。
        Vector3 respawnPosition;
        if (!TryFindRespawnPosition(deathPosition, out respawnPosition))
            respawnPosition = deathPosition;

        CurrentHealth = maxHealth;
        RestoreAliveState(respawnPosition, false, true);
    }

    private void RestoreAliveState(Vector3 position, bool network, bool resetAmmo)
    {
        // 恢复移动、武器、后坐力、动画和镜头状态。
        IsDead = false;
        respawnRemaining = 0f;
        transform.position = position;
        if (body != null)
        {
            body.isKinematic = network || originalBodyKinematic;
            body.useGravity = network ? false : originalUseGravity;
            body.position = position;
            if (!body.isKinematic)
            {
                body.velocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
        }
        if (weapon != null)
        {
            if (resetAmmo) weapon.ResetAmmo();
            // 菜单打开时收到服务器复活也保持武器关闭。
            weapon.enabled = !GameModeManager.IsGameplayPaused && !GameModeManager.IsMenuVisible;
        }
        if (recoil != null) recoil.enabled = true;
        if (animator != null) animator.speed = 1f;
        if (deathCamera != null)
        {
            deathCamera.transform.localPosition = cameraLocalPosition;
            deathCamera.transform.localRotation = cameraLocalRotation;
        }
    }

    /* 在死亡点周围寻找随机可行走位置，避开墙体和怪物。 */
    private bool TryFindRespawnPosition(Vector3 origin, out Vector3 result)
    {
        float minRadius = Mathf.Max(0f, respawnRadiusMin);
        float maxRadius = Mathf.Max(minRadius + 0.1f, respawnRadiusMax);
        int attempts = Mathf.Max(1, respawnAttempts);
        for (int i = 0; i < attempts; i++)
        {
            Vector2 random = Random.insideUnitCircle;
            if (random.sqrMagnitude < 0.001f) random = Vector2.right;
            random.Normalize();
            random *= Random.Range(minRadius, maxRadius);
            Vector3 candidate = origin + new Vector3(random.x, 0f, random.y);

            float groundY;
            if (!TryGetGroundHeight(candidate, out groundY)) continue;
            candidate.y = groundY + rootToGroundOffset;
            if (!IsRespawnAreaClear(candidate)) continue;
            result = candidate;
            return true;
        }
        result = origin;
        return false;
    }

    private bool TryGetGroundHeight(Vector3 position, out float groundY)
    {
        int mask = respawnGroundMask.value;
        if (mask == 0)
        {
            int groundLayer = LayerMask.NameToLayer("Ground");
            mask = groundLayer >= 0 ? 1 << groundLayer : Physics.DefaultRaycastLayers;
        }

        RaycastHit[] hits = Physics.RaycastAll(position + Vector3.up * 60f,
            Vector3.down, 120f, mask, QueryTriggerInteraction.Ignore);
        float best = float.NegativeInfinity;
        bool found = false;
        for (int i = 0; i < hits.Length; i++)
        {
            Collider hitCollider = hits[i].collider;
            if (hitCollider == null || hitCollider.transform == transform ||
                hitCollider.transform.IsChildOf(transform)) continue;
            if (hits[i].normal.y < 0.35f) continue;
            if (!found || hits[i].point.y > best)
            {
                best = hits[i].point.y;
                found = true;
            }
        }

        // 地形未设 Ground 层时使用 Terrain 采样。
        if (!found && Terrain.activeTerrain != null)
        {
            Terrain terrain = Terrain.activeTerrain;
            Vector3 local = position - terrain.transform.position;
            if (local.x >= 0f && local.z >= 0f &&
                local.x <= terrain.terrainData.size.x && local.z <= terrain.terrainData.size.z)
            {
                best = terrain.SampleHeight(position) + terrain.transform.position.y;
                found = true;
            }
        }
        groundY = best;
        return found;
    }

    private bool IsRespawnAreaClear(Vector3 position)
    {
        // 用玩家胶囊体检查墙体、道具、玩家和怪物重叠。
        // 使用玩家碰撞体范围做保守检测。
        float radius = 0.55f;
        if (playerCollider != null)
        {
            Bounds bounds = playerCollider.bounds;
            radius = Mathf.Clamp(Mathf.Max(bounds.extents.x, bounds.extents.z) * 0.9f, 0.35f, 1.1f);
        }
        Collider[] overlaps = Physics.OverlapCapsule(
            position + Vector3.up * 0.15f,
            position + Vector3.up * 1.8f,
            radius, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < overlaps.Length; i++)
        {
            Collider other = overlaps[i];
            if (other == null || other == playerCollider ||
                other.transform == transform || other.transform.IsChildOf(transform)) continue;
            // 胶囊体与脚下地面重叠属于正常情况。
            if (other is TerrainCollider || other.gameObject.layer == LayerMask.NameToLayer("Ground"))
                continue;
            // 避免生成在怪物内部。
            if (other.GetComponentInParent<EnemyControl>() != null) return false;
            // 其他碰撞体视为墙体、道具或玩家。
            return false;
        }
        return true;
    }
}
