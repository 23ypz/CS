using System;
using System.Collections;
using UnityEngine;

/* Unity 与 Python 共用的单层碰撞网格，统一使用世界坐标脚底位置。 */
[Serializable]
public class NetworkMapData
{
    public int width, depth; /* 网格列数、行数。 */
    public float originX, originZ, cell = 1f;
    public int spawn;
    public float[] heights; /* 每格地面世界高度。 */
    public int[] walkable; /* 1 为可走，0 为阻挡。 */

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
        /* 预测阶段：用与服务端相同的 30Hz 步长处理移动、跳跃和落地。 */
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
        /* 采集阶段：逐格射线检测地面和障碍，期间让出主线程避免卡顿。 */
        const int size = 81;
        NetworkMapData mapData = new NetworkMapData {
            width = size, depth = size, cell = 1f,
            originX = Mathf.Floor(player.position.x) - size / 2,
            originZ = Mathf.Floor(player.position.z) - size / 2,
            heights = new float[size * size], walkable = new int[size * size]
        };
        int mask = LayerMask.GetMask("Ground");
        Collider[] hits = new Collider[32];
        Physics.SyncTransforms();
        for (int i = 0; i < mapData.walkable.Length; i++)
        {
            Vector3 sample = new Vector3(mapData.originX + i % size, player.position.y, mapData.originZ + i / size);
            RaycastHit hit;
            if (Physics.Raycast(sample + Vector3.up * 50f, Vector3.down, out hit, 100f, mask, QueryTriggerInteraction.Ignore))
            {
                sample.y = hit.point.y;
                mapData.heights[i] = Mathf.Round(sample.y * 1000f) / 1000f;
                // 只把接近竖直的面当墙，丘陵坡面允许怪物行走。
                bool blocked = hit.normal.y < .45f;
                int hitCount = Physics.OverlapCapsuleNonAlloc(sample + Vector3.up * .48f, sample + Vector3.up * 1.4f,
                    .38f, hits, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
                if (hitCount == hits.Length) blocked = true;
                for (int c = 0; c < hitCount; c++)
                {
                    Collider obstacle = hits[c];
                    if (obstacle.transform == player || obstacle.transform.IsChildOf(player) || obstacle.GetComponentInParent<EnemyControl>() != null ||
                        obstacle.GetComponentInParent<MonsterAI>() != null || obstacle is TerrainCollider) continue;
                    if (obstacle.bounds.max.y > sample.y + .3f) blocked = true;
                }
                mapData.walkable[i] = blocked ? 0 : 1;
            }
            if (i % 100 == 0) { progress?.Invoke((float)i / mapData.walkable.Length); yield return null; }
        }
        mapData.spawn = mapData.Index(player.position.x, player.position.z);
        done(mapData);
    }
}
