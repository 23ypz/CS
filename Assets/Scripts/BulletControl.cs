using UnityEngine;

public class BulletControl : MonoBehaviour
{
    public float speed = 30f;
    public GameObject effectPrefab;

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
        // If the bullet hits a destructible object.
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

        // Hitboxes can be child objects, so find health on the parent hierarchy.
        EnemyControl enemy = collision.collider.GetComponentInParent<EnemyControl>();
        if (enemy != null)
            enemy.Gethit(2);

        if (effectPrefab != null && collision.contacts.Length > 0)
        {
            var go = Instantiate(effectPrefab, transform.position,
                Quaternion.LookRotation(collision.contacts[0].normal));
            Destroy(go, 1f);
        }

        Destroy(gameObject);
    }
}
