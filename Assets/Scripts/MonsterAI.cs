using UnityEngine;

public class MonsterAI : MonoBehaviour
{
    public float moveSpeed = 2.5f;
    public float stopDistance = 1.5f;

    [Header("Attack")]
    [Tooltip("Players within this horizontal radius take damage once per second.")]
    // Match the authoritative multiplayer range so single-player and
    // multiplayer have the same close-range threat distance.
    public float attackRadius = 2.2f;
    public float attackDamage = 10f;
    public float attackInterval = 1f;

    [Tooltip("How quickly projectile knockback fades, in metres per second squared.")]
    public float knockbackDamping = 10f;

    [Tooltip("Maximum accumulated horizontal knockback speed.")]
    public float maxKnockbackSpeed = 5f;

    [Header("Obstacle avoidance")]
    [Tooltip("Radius used to probe walls in front of the monster.")]
    [Min(0.1f)]
    public float obstacleRadius = 0.45f;
    [Tooltip("Height of the horizontal wall probe above the monster's feet.")]
    [Min(0.1f)]
    public float obstacleProbeHeight = 0.7f;
    [Tooltip("Extra clearance kept between the monster and a wall.")]
    [Min(0f)]
    public float obstacleSkin = 0.05f;

    [Tooltip("Local grid spacing used when a wall blocks direct pursuit.")]
    [Min(0.25f)]
    public float navigationCellSize = 0.8f;
    [Tooltip("Number of cells searched in each direction around the monster.")]
    [Range(5, 61)]
    public int navigationGridSize = 31;
    [Tooltip("Seconds between local grid path rebuilds.")]
    [Min(0.05f)]
    public float navigationRepathInterval = 0.25f;

    private Rigidbody body;
    private Transform target;
    private LayerMask groundMask;
    private float groundOffset;
    private Vector3 knockbackVelocity;
    private float nextAttackTime;
    private Vector3 cachedPathDirection;
    private float nextPathRebuildTime;
    private Vector3 cachedPathTarget;
    private readonly RaycastHit[] castHits = new RaycastHit[16];
    private readonly RaycastHit[] groundHits = new RaycastHit[8];
    private readonly Collider[] overlapHits = new Collider[16];
    // The monster can start on a large height difference (for example when
    // spawning on a city ramp), so a short ray can miss the terrain and leave
    // the old Y coordinate unchanged. Keep the ray deliberately generous.
    private const float GroundRayHeight = 50f;
    private const float GroundRayDistance = 100f;

    public void Initialize(Transform player, float speed, LayerMask mask)
    {
        target = player;
        moveSpeed = speed;
        groundMask = mask;
    }

    public void SetGroundOffset(float offset)
    {
        groundOffset = offset;
    }

    /// <summary>
    /// Applies a horizontal projectile impulse.  Monsters use kinematic bodies
    /// for deterministic navigation, so the impulse is integrated here instead
    /// of calling Rigidbody.AddForce on the kinematic body.
    /// </summary>
    public void ApplyKnockback(Vector3 impulse)
    {
        impulse.y = 0f;
        knockbackVelocity += impulse;
        float maxSpeed = Mathf.Max(0f, maxKnockbackSpeed);
        if (maxSpeed > 0f && knockbackVelocity.sqrMagnitude > maxSpeed * maxSpeed)
            knockbackVelocity = knockbackVelocity.normalized * maxSpeed;
    }

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
        if (body == null)
            body = gameObject.AddComponent<Rigidbody>();

        body.isKinematic = true;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.constraints = RigidbodyConstraints.FreezeRotationX |
                           RigidbodyConstraints.FreezeRotationZ;
    }

    private void FixedUpdate()
    {
        if (target == null)
            return;

        Vector3 offset = target.position - body.position;
        offset.y = 0f;
        float distance = offset.magnitude;
        Vector3 nextPosition = body.position;
        Quaternion facing = body.rotation;

        // Blend the impulse with the normal pursuit movement.  This gives a
        // visible brief retreat while keeping the existing obstacle-aware AI.
        Vector3 knockbackStep = Vector3.zero;
        float pursuitScale = 1f;
        if (knockbackVelocity.sqrMagnitude > 0.0001f)
        {
            knockbackStep = knockbackVelocity * Time.fixedDeltaTime;
            // Give the impulse a clear visual retreat instead of letting the
            // regular chase speed cancel it on the same physics step.
            pursuitScale = knockbackVelocity.magnitude > 0.25f ? 0.35f : 1f;
            float damping = Mathf.Max(0f, knockbackDamping) * Time.fixedDeltaTime;
            knockbackVelocity = Vector3.MoveTowards(knockbackVelocity, Vector3.zero, damping);
        }
        nextPosition += knockbackStep;

        if (distance <= Mathf.Max(0.1f, attackRadius) && Time.time >= nextAttackTime)
        {
            PlayerHealth health = target.GetComponentInParent<PlayerHealth>();
            if (health != null && !health.IsDead)
            {
                health.TakeDamage(Mathf.Max(0f, attackDamage));
                nextAttackTime = Time.time + Mathf.Max(0.05f, attackInterval);
            }
        }

        if (distance > stopDistance)
        {
            Vector3 direction = offset / distance;
            facing = Quaternion.LookRotation(direction, Vector3.up);
            nextPosition += direction * moveSpeed * Time.fixedDeltaTime * pursuitScale;
        }

        // Monsters previously moved directly to the target and could therefore
        // pass through buildings and other colliders. Resolve the complete
        // horizontal step with a sphere probe and, when blocked, choose a clear
        // tangent direction around the obstacle. This keeps the existing
        // deterministic kinematic movement while providing lightweight
        // obstacle avoidance without requiring a baked NavMesh.
        Vector3 horizontalStep = nextPosition - body.position;
        horizontalStep.y = 0f;
        horizontalStep = GetCollisionFreeStep(body.position, horizontalStep);
        nextPosition = body.position + horizontalStep;

        // Snap every physics step, including while stopped near the player.
        // This prevents a monster from retaining an invalid Y after walking
        // over a slope or after a temporary raycast miss.
        if (TryGetGroundY(nextPosition, out float groundY))
            nextPosition.y = groundY + groundOffset;

        body.MoveRotation(facing);
        body.MovePosition(nextPosition);
    }

    private Vector3 GetCollisionFreeStep(Vector3 from, Vector3 desiredStep)
    {
        desiredStep.y = 0f;
        float distance = desiredStep.magnitude;
        if (distance <= 0.0001f)
            return Vector3.zero;

        Vector3 desiredDirection = desiredStep / distance;
        float probeRadius = Mathf.Max(0.1f, obstacleRadius);
        Vector3 probeOrigin = from + Vector3.up * Mathf.Max(probeRadius, obstacleProbeHeight);

        if (!HasObstacle(probeOrigin, desiredDirection, distance + obstacleSkin, probeRadius))
            return desiredStep;

        // Directional steering is not sufficient at concave wall corners: it
        // can keep selecting the same blocked tangent forever. Build a small
        // local A* path through free cells and use its first waypoint. This is
        // deliberately local and rebuilt periodically, so it works without a
        // baked NavMesh and remains deterministic for each monster.
        if (TryGetGridPathDirection(from, distance, out Vector3 gridDirection))
            return gridDirection * distance;

        // There is deliberately no reduced-radius escape or normal push here:
        // those shortcuts can move a monster through a wall at a corner. If a
        // complete-radius grid path is unavailable, remain still until the
        // next rebuild rather than violating collision clearance.
        return Vector3.zero;
    }

    private bool TryGetGridPathDirection(Vector3 from, float stepDistance,
        out Vector3 direction)
    {
        direction = Vector3.zero;
        if (target == null)
            return false;

        Vector3 targetFlat = target.position;
        targetFlat.y = from.y;
        Vector3 toTarget = targetFlat - from;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.25f)
            return false;

        bool targetChanged = (targetFlat - cachedPathTarget).sqrMagnitude > 1f;
        if (Time.time >= nextPathRebuildTime || targetChanged || cachedPathDirection.sqrMagnitude < 0.01f)
        {
            cachedPathTarget = targetFlat;
            nextPathRebuildTime = Time.time + Mathf.Max(0.05f, navigationRepathInterval);
            cachedPathDirection = BuildLocalGridDirection(from, targetFlat);
        }

        if (cachedPathDirection.sqrMagnitude < 0.01f)
            return false;
        direction = cachedPathDirection.normalized;
        Vector3 probeOrigin = from + Vector3.up * Mathf.Max(obstacleRadius, obstacleProbeHeight);
        float probeRadius = Mathf.Max(0.1f, obstacleRadius);
        if (HasObstacle(probeOrigin, direction, stepDistance + obstacleSkin, probeRadius))
        {
            // Invalidate immediately; the next physics tick will rebuild from
            // the new position rather than repeatedly pushing into a corner.
            cachedPathDirection = Vector3.zero;
            return false;
        }
        return true;
    }

    private Vector3 BuildLocalGridDirection(Vector3 from, Vector3 targetFlat)
    {
        int size = Mathf.Clamp(navigationGridSize, 5, 61);
        if ((size & 1) == 0) size++;
        int center = size / 2;
        float cell = Mathf.Max(0.25f, navigationCellSize);
        int count = size * size;
        bool[] blocked = new bool[count];
        float[] g = new float[count];
        float[] f = new float[count];
        int[] cameFrom = new int[count];
        bool[] open = new bool[count];
        bool[] closed = new bool[count];
        for (int i = 0; i < count; i++)
        {
            g[i] = float.PositiveInfinity;
            f[i] = float.PositiveInfinity;
            cameFrom[i] = -1;
            int x = i % size;
            int z = i / size;
            Vector3 p = from + new Vector3((x - center) * cell, 0f, (z - center) * cell);
            blocked[i] = IsGridCellBlocked(p, from.y);
        }
        int start = center + center * size;
        blocked[start] = false;
        Vector3 targetOffset = targetFlat - from;
        int goalX = Mathf.Clamp(Mathf.RoundToInt(targetOffset.x / cell) + center, 0, size - 1);
        int goalZ = Mathf.Clamp(Mathf.RoundToInt(targetOffset.z / cell) + center, 0, size - 1);
        int goal = goalX + goalZ * size;
        if (blocked[goal])
        {
            // A player can be standing inside the target cell; choose the
            // nearest free cell to it as the temporary goal.
            float nearest = float.PositiveInfinity;
            int freeGoal = -1;
            for (int i = 0; i < count; i++)
            {
                if (blocked[i]) continue;
                int x = i % size, z = i / size;
                float d = (x - goalX) * (x - goalX) + (z - goalZ) * (z - goalZ);
                if (d < nearest) { nearest = d; freeGoal = i; }
            }
            if (freeGoal < 0) return Vector3.zero;
            goal = freeGoal;
        }

        g[start] = 0f;
        f[start] = GridHeuristic(start, goal, size);
        open[start] = true;
        int reached = start;
        int[] dx = { 1, -1, 0, 0, 1, 1, -1, -1 };
        int[] dz = { 0, 0, 1, -1, 1, -1, 1, -1 };
        while (true)
        {
            int current = -1;
            float best = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
                if (open[i] && !closed[i] && f[i] < best) { best = f[i]; current = i; }
            if (current < 0) break;
            if (current == goal) { reached = current; break; }
            open[current] = false;
            closed[current] = true;
            if (GridHeuristic(current, goal, size) < GridHeuristic(reached, goal, size)) reached = current;
            int cx = current % size, cz = current / size;
            for (int n = 0; n < dx.Length; n++)
            {
                int nx = cx + dx[n], nz = cz + dz[n];
                if (nx < 0 || nx >= size || nz < 0 || nz >= size) continue;
                int next = nx + nz * size;
                if (blocked[next] || closed[next]) continue;
                // Do not cut diagonally through a wall corner.
                if (n >= 4 &&
                    (blocked[(cx + dx[n]) + cz * size] || blocked[cx + (cz + dz[n]) * size])) continue;
                float tentative = g[current] + ((n < 4) ? 1f : 1.4142f);
                if (!open[next] || tentative < g[next])
                {
                    cameFrom[next] = current;
                    g[next] = tentative;
                    f[next] = tentative + GridHeuristic(next, goal, size);
                    open[next] = true;
                }
            }
        }
        if (reached == start || cameFrom[reached] < 0)
            return Vector3.zero;
        int first = reached;
        while (cameFrom[first] >= 0 && cameFrom[first] != start)
            first = cameFrom[first];
        int fx = first % size, fz = first / size;
        Vector3 waypoint = from + new Vector3((fx - center) * cell, 0f, (fz - center) * cell);
        Vector3 result = waypoint - from;
        result.y = 0f;
        return result.sqrMagnitude > 0.001f ? result.normalized : Vector3.zero;
    }

    private float GridHeuristic(int a, int b, int size)
    {
        int ax = a % size, az = a / size, bx = b % size, bz = b / size;
        return Mathf.Abs(ax - bx) + Mathf.Abs(az - bz);
    }

    private bool IsGridCellBlocked(Vector3 position, float baseY)
    {
        // Use the same footprint as the movement SphereCast (plus the skin).
        // A smaller planning radius produces waypoints that look free to A*
        // but are rejected by the real movement probe at wall corners.
        float radius = Mathf.Max(0.1f, obstacleRadius) + Mathf.Max(0f, obstacleSkin);
        Vector3 origin = new Vector3(position.x, baseY + Mathf.Max(radius, obstacleProbeHeight), position.z);
        int count = Physics.OverlapSphereNonAlloc(origin, radius, overlapHits,
            Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
            if (overlapHits[i] != null && !IsIgnoredCollider(overlapHits[i])) return true;
        return false;
    }

    private bool HasObstacle(Vector3 origin, Vector3 direction, float distance, float radius)
    {
        int count = Physics.SphereCastNonAlloc(
            origin,
            radius,
            direction,
            castHits,
            Mathf.Max(0f, distance),
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        float nearest = float.PositiveInfinity;
        bool blocked = false;
        for (int i = 0; i < count; i++)
        {
            Collider collider = castHits[i].collider;
            if (collider == null || IsIgnoredCollider(collider))
                continue;

            if (castHits[i].distance < nearest)
            {
                nearest = castHits[i].distance;
                blocked = true;
            }
        }
        return blocked;
    }

    private bool IsIgnoredCollider(Collider collider)
    {
        Transform root = collider.transform.root;
        if (root == transform.root)
            return true;
        if (target != null && root == target.root)
            return true;

        // Other monsters and player hitboxes are dynamic actors, not walls.
        // Ignoring them prevents a group of monsters from deadlocking in front
        // of one another while still allowing static level geometry to block.
        if (collider.GetComponentInParent<EnemyControl>() != null ||
            collider.GetComponentInParent<PlayerHealth>() != null)
            return true;
        return false;
    }

    private bool TryGetGroundY(Vector3 position, out float groundY)
    {
        // Query all normal physics layers so raised roads/platforms on the
        // Default layer are treated as ground too. The configured Ground layer
        // remains supported, while filtering by surface normal avoids walls.
        RaycastHit nearestHit;
        if (Physics.Raycast(position + Vector3.up * GroundRayHeight, Vector3.down,
            out nearestHit, GroundRayDistance, Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore) && nearestHit.normal.y >= 0.35f &&
            !IsIgnoredCollider(nearestHit.collider) &&
            nearestHit.collider.GetComponentInParent<EnemyControl>() == null)
        {
            groundY = nearestHit.point.y;
            return true;
        }
        int count = Physics.RaycastNonAlloc(
            position + Vector3.up * GroundRayHeight,
            Vector3.down,
            groundHits,
            GroundRayDistance,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        float highestGround = float.NegativeInfinity;
        bool foundGround = false;
        for (int i = 0; i < count; i++)
        {
            Transform hitTransform = groundHits[i].collider.transform;
            if (hitTransform.root == transform.root ||
                (target != null && hitTransform.root == target.root))
                continue;

            EnemyControl hitEnemy = groundHits[i].collider.GetComponentInParent<EnemyControl>();
            if (hitEnemy != null)
                continue;

            if (groundHits[i].normal.y < 0.35f)
                continue;

            if (!foundGround || groundHits[i].point.y > highestGround)
            {
                highestGround = groundHits[i].point.y;
                foundGround = true;
            }
        }

        if (foundGround)
        {
            groundY = highestGround;
            return true;
        }

        // Terrain.SampleHeight also works when a terrain collider is disabled
        // or temporarily unavailable to the physics query.
        Terrain terrain = Terrain.activeTerrain;
        if (terrain != null && terrain.terrainData != null)
        {
            Vector3 local = position - terrain.transform.position;
            Vector3 size = terrain.terrainData.size;
            if (local.x >= 0f && local.x <= size.x &&
                local.z >= 0f && local.z <= size.z)
            {
                groundY = terrain.SampleHeight(position) + terrain.transform.position.y;
                return true;
            }
        }

        groundY = 0f;
        return false;
    }
}
