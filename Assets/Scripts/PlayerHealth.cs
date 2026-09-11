using UnityEngine;

/// <summary>Local player health, damage and the short death/respawn state.</summary>
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

    private float respawnRemaining;
    private Vector3 deathPosition;
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

    private void Awake()
    {
        maxHealth = Mathf.Max(1f, maxHealth);
        CurrentHealth = maxHealth;
        body = GetComponent<Rigidbody>();
        animator = GetComponentInChildren<Animator>(true);
        controller = GetComponent<PlayerControl>();
        weapon = GetComponent<WeaponControl>();
        recoil = GetComponent<RecoilControl>();
        playerCollider = GetComponent<Collider>();
        if (playerCollider != null)
        {
            // Keep the same root-to-feet offset when moving to a new ground
            // position. This avoids putting the feet below a sloped floor.
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
        if (IsDead || amount <= 0f) return;
        CurrentHealth = Mathf.Max(0f, CurrentHealth - amount);
        if (CurrentHealth <= 0f) Die();
    }

    /// <summary>Apply the server-authoritative state used by multiplayer.</summary>
    public void ApplyAuthoritativeState(int health, int maximum, bool dead, float remaining)
    {
        maxHealth = Mathf.Max(1f, maximum);
        CurrentHealth = Mathf.Clamp(health, 0, Mathf.RoundToInt(maxHealth));
        if (dead)
        {
            if (!IsDead)
                Die();
            respawnRemaining = Mathf.Clamp(remaining, 0f, Mathf.Max(0.1f, respawnDuration));
            return;
        }
        if (IsDead)
            Respawn();
    }

    private void Die()
    {
        if (IsDead) return;
        IsDead = true;
        respawnRemaining = Mathf.Max(0.1f, respawnDuration);
        deathPosition = transform.position;
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
            body.isKinematic = true;
        }
        if (weapon != null) weapon.enabled = false;
        if (recoil != null) recoil.enabled = false;
        if (animator != null) animator.speed = 0f;
        if (deathCamera != null) deathCamera.transform.position = deathCamera.transform.position;
    }

    private void Update()
    {
        if (!IsDead) return;
        transform.position = deathPosition;
        respawnRemaining -= Time.unscaledDeltaTime;
        if (deathCamera != null)
        {
            float t = RespawnProgress;
            // Roll/pitch the view down over the two-second death window while
            // keeping the camera anchored at the death location.
            deathCamera.transform.localPosition = cameraLocalPosition + Vector3.down * (0.25f * t);
            deathCamera.transform.localRotation = cameraLocalRotation *
                Quaternion.Euler(72f * t, 0f, 0f);
        }
        if (respawnRemaining <= 0f) Respawn();
    }

    private void Respawn()
    {
        Vector3 respawnPosition;
        if (!TryFindRespawnPosition(deathPosition, out respawnPosition))
            respawnPosition = deathPosition;

        IsDead = false;
        // A respawn is always a fresh life, even if the last server/local
        // update arrived with a stale zero-health value.
        CurrentHealth = maxHealth;
        respawnRemaining = 0f;
        transform.position = respawnPosition;
        if (body != null)
        {
            body.isKinematic = false;
            body.position = respawnPosition;
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        if (weapon != null) weapon.enabled = true;
        if (recoil != null) recoil.enabled = true;
        if (animator != null) animator.speed = 1f;
        if (deathCamera != null)
        {
            deathCamera.transform.localPosition = cameraLocalPosition;
            deathCamera.transform.localRotation = cameraLocalRotation;
        }
    }

    /// <summary>
    /// Finds a random walkable point around the death location. A ground ray
    /// and a small occupancy test keep the player out of walls, monsters and
    /// other solid geometry. Falling back to the death point preserves play
    /// on scenes that do not expose a Ground layer.
    /// </summary>
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

        // Terrain may not have been assigned to a Ground layer. Sample it as
        // a fallback so the random respawn still works on the city terrain.
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
        // Use the current collider footprint as a conservative clearance test.
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
            // The clearance capsule can overlap the floor when the player
            // root is close to its feet. That is expected, not an obstacle.
            if (other is TerrainCollider || other.gameObject.layer == LayerMask.NameToLayer("Ground"))
                continue;
            // Monsters are dynamic hazards; avoid spawning directly inside one.
            if (other.GetComponentInParent<EnemyControl>() != null) return false;
            // Ground underneath does not intersect the raised capsule. Any
            // other collider indicates a wall, prop, or another player.
            return false;
        }
        return true;
    }
}
