using UnityEngine;

public class BulletControl : MonoBehaviour
{
    public float speed = 30f; // 发射冲量大小
    public GameObject effectPrefab; // 命中特效

    private Rigidbody rb;

    private void Start()
    {
        rb = GetComponent<Rigidbody>();
        if (rb != null)
            rb.AddForce(transform.forward * speed, ForceMode.Impulse);

        Destroy(gameObject, 1f);
    }

    private void OnCollisionEnter(Collision collision)
    {
        /* 子弹命中可破坏目标。 */
        if (collision.gameObject.CompareTag("des"))
        {
            Rigidbody rbody = collision.gameObject.GetComponent<Rigidbody>();
            if (rbody == null)
                rbody = collision.gameObject.AddComponent<Rigidbody>();

            rbody.AddForceAtPosition(transform.forward * 3f, collision.contacts[0].point,
                ForceMode.Impulse);
            Destroy(collision.gameObject.GetComponent<Collider>(), 0.04f);
            Destroy(collision.gameObject, 2f);
        }

        /* 命中体可能是子物体，从父级查找生命组件。 */
        EnemyControl enemy = collision.collider.GetComponentInParent<EnemyControl>();
        if (enemy != null)
            enemy.Gethit(2, transform.forward);

        if (effectPrefab != null && collision.contacts.Length > 0)
        {
            var go = Instantiate(effectPrefab, transform.position,
                Quaternion.LookRotation(collision.contacts[0].normal));
            Destroy(go, 1f);
        }

        Destroy(gameObject);
    }
}
