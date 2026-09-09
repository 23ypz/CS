using UnityEngine;

public class EnemyControl : MonoBehaviour
{
    public int hp = 10;
    public GameObject bombEffect;

    private bool dead;

    public void Gethit(int damage)
    {
        if (dead)
            return;

        hp -= damage;
        if (hp > 0)
            return;

        dead = true;

        if (bombEffect != null)
            Instantiate(bombEffect, transform.position, transform.rotation);

        // EnemyControl may be placed on a child hitbox; remove the whole monster.
        Destroy(transform.root.gameObject);
    }
}
