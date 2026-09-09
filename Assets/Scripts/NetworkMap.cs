using System;
using System.Collections;
using UnityEngine;

// A small, single-level collision grid shared by Python and Unity. World-space
// feet coordinates are used everywhere; model-origin offsets are visual only.
[Serializable]
public class NetworkMapData
{
    public int width, depth;
    public float originX, originZ, cell = 1f;
    public int spawn;
    public float[] heights;
    public int[] walkable;

    public int Index(float x, float z)
    {
        int ix = Mathf.FloorToInt((x - originX) / cell + .5f);
        int iz = Mathf.FloorToInt((z - originZ) / cell + .5f);
        return ix < 0 || iz < 0 || ix >= width || iz >= depth ? -1 : iz * width + ix;
    }

    public bool CanMove(float x, float z, float oldGround)
    {
        int i = Index(x, z);
        return i >= 0 && walkable[i] != 0 && Mathf.Abs(heights[i] - oldGround) <= .5f;
    }

    public void Step(ref Vector3 position, ref float vy, NetInput input)
    {
        int old = Index(position.x, position.z);
        if (old < 0) return;
        const float dt = 1f / 30f;
        float ground = heights[old];
        bool grounded = position.y <= ground + .03f && vy <= 0f;
        float magnitude = Mathf.Max(1f, Mathf.Sqrt(input.x * input.x + input.z * input.z));
        float yaw = input.yaw * Mathf.Deg2Rad;
        float speed = input.run && grounded ? 5f : 3f;
        float dx = (Mathf.Cos(yaw) * input.x + Mathf.Sin(yaw) * input.z) / magnitude * speed * dt;
        float dz = (-Mathf.Sin(yaw) * input.x + Mathf.Cos(yaw) * input.z) / magnitude * speed * dt;
        if (CanMove(position.x + dx, position.z, ground)) position.x += dx;
        if (CanMove(position.x, position.z + dz, ground)) position.z += dz;
        if (grounded && input.jump) vy = 5f;
        vy -= 9.81f * dt;
        position.y += vy * dt;
        ground = heights[Index(position.x, position.z)];
        if (position.y < ground) { position.y = ground; vy = 0f; }
    }
}

public static class NetworkMap
{
    public static IEnumerator Capture(Transform player, Action<NetworkMapData> done, Action<float> progress)
    {
        const int size = 81;
        NetworkMapData data = new NetworkMapData {
            width = size, depth = size, cell = 1f,
            originX = Mathf.Floor(player.position.x) - size / 2,
            originZ = Mathf.Floor(player.position.z) - size / 2,
            heights = new float[size * size], walkable = new int[size * size]
        };
        int groundMask = LayerMask.GetMask("Ground");
        Collider[] overlaps = new Collider[32];
        Physics.SyncTransforms();
        for (int i = 0; i < data.walkable.Length; i++)
        {
            Vector3 point = new Vector3(data.originX + i % size, player.position.y, data.originZ + i / size);
            RaycastHit ground;
            if (Physics.Raycast(point + Vector3.up * 50f, Vector3.down, out ground, 100f, groundMask, QueryTriggerInteraction.Ignore))
            {
                point.y = ground.point.y;
                data.heights[i] = Mathf.Round(point.y * 1000f) / 1000f;
                bool blocked = ground.normal.y < .7f;
                int n = Physics.OverlapCapsuleNonAlloc(point + Vector3.up * .48f, point + Vector3.up * 1.4f,
                    .38f, overlaps, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                if (n == overlaps.Length) blocked = true;
                for (int c = 0; c < n; c++)
                {
                    Collider hit = overlaps[c];
                    if (hit.transform == player || hit.transform.IsChildOf(player) || hit.GetComponentInParent<EnemyControl>() != null ||
                        hit.GetComponentInParent<MonsterAI>() != null || hit is TerrainCollider) continue;
                    if (hit.bounds.max.y > point.y + .3f) blocked = true;
                }
                data.walkable[i] = blocked ? 0 : 1;
            }
            if (i % 100 == 0) { progress?.Invoke((float)i / data.walkable.Length); yield return null; }
        }
        data.spawn = data.Index(player.position.x, player.position.z);
        done(data);
    }
}
