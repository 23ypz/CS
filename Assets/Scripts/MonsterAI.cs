using UnityEngine;

public class MonsterAI : MonoBehaviour
{
    public float moveSpeed = 2.5f;
    public float stopDistance = 1.5f;

    [Header("攻击")]
    [Tooltip("水平攻击范围，范围内玩家每秒受伤一次。")]
    /* 与多人服务器保持相同攻击范围。 */
    public float attackRadius = 2.2f;
    public float attackDamage = 10f;
    public float attackInterval = 1f;

    [Tooltip("击退速度每秒的衰减量。")]
    public float knockbackDamping = 10f;

    [Tooltip("水平击退速度的叠加上限。")]
    public float maxKnockbackSpeed = 5f;

    [Header("绕障")]
    [Tooltip("探测前方墙体的半径。")]
    [Min(0.1f)]
    public float obstacleRadius = 0.45f;
    [Tooltip("墙体探测起点距脚底的高度。")]
    [Min(0.1f)]
    public float obstacleProbeHeight = 0.7f;
    [Tooltip("怪物与墙体之间额外保留的间距。")]
    [Min(0f)]
    public float obstacleSkin = 0.05f;

    [Tooltip("绕障寻路网格的间距。")]
    [Min(0.25f)]
    public float navigationCellSize = 0.8f;
    [Tooltip("局部寻路网格的边长，单位为格。")]
    [Range(5, 61)]
    public int navigationGridSize = 31;
    [Tooltip("重新寻路的间隔秒数。")]
    [Min(0.05f)]
    public float navigationRepathInterval = 0.25f;

    private Rigidbody body;
    private Transform target;
    private LayerMask groundMask;
    private float groundOffset;
    private Vector3 knockbackVelocity;
    private float nextAttackTime;
    private Vector3 cachedPathDirection; // 缓存绕障方向
    private float nextPathRebuildTime; // 下次路径重建时间
    private Vector3 cachedPathTarget;
    private readonly RaycastHit[] castHits = new RaycastHit[16];
    private readonly RaycastHit[] groundHits = new RaycastHit[8];
    private readonly Collider[] overlapHits = new Collider[16];
    /* 城市斜坡高度差较大，使用足够长的地面射线。 */
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

    /* 施加水平子弹冲量；运动由运动学刚体确定性积分。 */
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

        /* 阶段一：合并击退和追踪移动，保留可见后退效果。 */
        Vector3 knockbackStep = Vector3.zero;
        float pursuitScale = 1f;
        if (knockbackVelocity.sqrMagnitude > 0.0001f)
        {
            knockbackStep = knockbackVelocity * Time.fixedDeltaTime;
            /* 减弱追踪速度，避免同一帧抵消击退。 */
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

        /* 阶段二：用球形检测解析水平步进，阻挡时选择切向路径。 */
        Vector3 horizontalStep = nextPosition - body.position;
        horizontalStep.y = 0f;
        horizontalStep = GetCollisionFreeStep(body.position, horizontalStep);
        nextPosition = body.position + horizontalStep;

        /* 阶段三：校正地面高度，避免斜坡或射线漏检导致高度失效。 */
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

        /* 凹角处单纯转向可能反复卡住，因此构建局部 A* 路径。 */
        if (TryGetGridPathDirection(from, distance, out Vector3 gridDirection))
            return gridDirection * distance;

        /* 无完整路径时保持静止，避免缩小半径穿墙。 */
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
            /* 立即失效，下个物理帧从新位置重建路径。 */
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
        Vector3 goalOffset = targetFlat - from;
        int goalX = Mathf.Clamp(Mathf.RoundToInt(goalOffset.x / cell) + center, 0, size - 1);
        int goalZ = Mathf.Clamp(Mathf.RoundToInt(goalOffset.z / cell) + center, 0, size - 1);
        int goal = goalX + goalZ * size;
        if (blocked[goal])
        {
            /* 目标可能站在障碍格内，改用最近的可行格。 */
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
                /* 禁止从墙角对角穿过。 */
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
        /* 规划半径与 SphereCast 一致，避免墙角路径被实际检测拒绝。 */
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

        /* 忽略其他怪物和玩家碰撞体，避免怪群互相堵死；静态几何仍会阻挡。 */
        if (collider.GetComponentInParent<EnemyControl>() != null ||
            collider.GetComponentInParent<PlayerHealth>() != null)
            return true;
        return false;
    }

    private bool TryGetGroundY(Vector3 position, out float groundY)
    {
        /* 查询普通物理层以支持道路和平台，并按法线过滤墙面。 */
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

        /* 物理查询不可用时使用 Terrain.SampleHeight。 */
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
