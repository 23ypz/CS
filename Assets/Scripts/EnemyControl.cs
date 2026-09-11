using UnityEngine;

public class EnemyControl : MonoBehaviour
{
    public int hp = 10;
    public GameObject bombEffect;

    // Small impulse applied when a projectile hits this monster.  MonsterAI
    // keeps the movement authoritative in single-player while the server
    // applies the equivalent impulse for network monsters.
    [Min(0f)]
    public float knockbackForce = 2.2f;

    private bool dead;
    private bool networkControlled;

    /// <summary>
    /// Network monsters are damaged by the authoritative server. Their local
    /// visual proxies can still receive bullet collisions, but must not be
    /// removed before a server snapshot confirms the kill.
    /// </summary>
    public void SetNetworkControlled(bool value)
    {
        networkControlled = value;
    }

    public void Gethit(int damage)
    {
        Gethit(damage, Vector3.zero);
    }

    /// <summary>Damages the monster and applies a short horizontal knockback.</summary>
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

        // EnemyControl may be placed on a child hitbox; remove the whole monster.
        Destroy(transform.root.gameObject);
    }

    /// <summary>Creates and starts an explosion, then cleans it up after it ends.</summary>
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
