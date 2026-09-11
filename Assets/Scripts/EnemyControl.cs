using UnityEngine;

public class EnemyControl : MonoBehaviour
{
    public int hp = 10; // 当前血量
    public GameObject bombEffect; // 死亡爆炸预制体

    /* 子弹命中时施加水平击退；单机由 MonsterAI，联机由服务器同步。 */
    [Min(0f)]
    public float knockbackForce = 2.2f;

    private bool dead;
    private bool networkControlled;

    /* 联机怪物由服务器扣血，客户端代理等待快照确认后再移除。 */
    public void SetNetworkControlled(bool value)
    {
        networkControlled = value;
    }

    public void Gethit(int damage)
    {
        Gethit(damage, Vector3.zero);
    }

    /* 造成伤害并施加短暂水平击退。 */
    public void Gethit(int damage, Vector3 hitDirection)
    {
        if (dead || networkControlled)
            return;

        if (hitDirection.sqrMagnitude > 0.0001f)
        {
            hitDirection.y = 0f;
            if (hitDirection.sqrMagnitude > 0.0001f)
            {
                MonsterAI ai = GetComponentInParent<MonsterAI>();
                if (ai != null)
                    ai.ApplyKnockback(hitDirection.normalized * knockbackForce);
            }
        }

        hp -= damage;
        if (hp > 0)
            return;

        dead = true;
        SpawnDeathEffect(bombEffect, transform.root);
        /* 命中体可能在子物体上，因此移除整个怪物根对象。 */
        Destroy(transform.root.gameObject);
    }

    /* 创建爆炸特效，并在播放结束后清理。 */
    public static void SpawnDeathEffect(GameObject prefab, Transform target)
    {
        if (prefab == null || target == null)
            return;

        Vector3 position = target.position;
        Renderer[] renderers = target.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length > 0)
        {
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
                bounds.Encapsulate(renderers[i].bounds);
            position = bounds.center;
        }

        GameObject effect = Instantiate(prefab, position, target.rotation);
        effect.SetActive(true);

        float cleanupDelay = 2f;
        ParticleSystem[] particles = effect.GetComponentsInChildren<ParticleSystem>(true);
        for (int i = 0; i < particles.Length; i++)
        {
            ParticleSystem.MainModule main = particles[i].main;
            cleanupDelay = Mathf.Max(cleanupDelay,
                main.startDelay.constantMax + main.duration + main.startLifetime.constantMax);
            particles[i].Play(true);
        }

        AudioSource[] audioSources = effect.GetComponentsInChildren<AudioSource>(true);
        for (int i = 0; i < audioSources.Length; i++)
        {
            if (!audioSources[i].isPlaying)
                audioSources[i].Play();
            if (audioSources[i].clip != null)
                cleanupDelay = Mathf.Max(cleanupDelay, audioSources[i].clip.length);
        }

        Destroy(effect, cleanupDelay + 0.25f);
    }
}
