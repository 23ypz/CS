using UnityEngine;

public class MonsterAI : MonoBehaviour
{
    public float moveSpeed = 2.5f;
    public float stopDistance = 1.5f;

    private Rigidbody body;
    private Transform target;
    private LayerMask groundMask;
    private float groundOffset;
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

        if (distance > stopDistance)
        {
            Vector3 direction = offset / distance;
            facing = Quaternion.LookRotation(direction, Vector3.up);
            nextPosition += direction * moveSpeed * Time.fixedDeltaTime;
        }

        // Snap every physics step, including while stopped near the player.
        // This prevents a monster from retaining an invalid Y after walking
        // over a slope or after a temporary raycast miss.
        if (TryGetGroundY(nextPosition, out float groundY))
            nextPosition.y = groundY + groundOffset;

        body.MoveRotation(facing);
        body.MovePosition(nextPosition);
    }

    private bool TryGetGroundY(Vector3 position, out float groundY)
    {
        // Query all normal physics layers so raised roads/platforms on the
        // Default layer are treated as ground too. The configured Ground layer
        // remains supported, while filtering by surface normal avoids walls.
        RaycastHit[] hits = Physics.RaycastAll(
            position + Vector3.up * GroundRayHeight,
            Vector3.down,
            GroundRayDistance,
            Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
        float highestGround = float.NegativeInfinity;
        bool foundGround = false;
        for (int i = 0; i < hits.Length; i++)
        {
            Transform hitTransform = hits[i].collider.transform;
            if (hitTransform.root == transform.root ||
                (target != null && hitTransform.root == target.root))
                continue;

            EnemyControl hitEnemy = hits[i].collider.GetComponentInParent<EnemyControl>();
            if (hitEnemy != null)
                continue;

            if (hits[i].normal.y < 0.35f)
                continue;

            if (!foundGround || hits[i].point.y > highestGround)
            {
                highestGround = hits[i].point.y;
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
