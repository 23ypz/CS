using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class BulletControl : MonoBehaviour
{

    public float speed = 30;

    private Rigidbody rb;
    public GameObject effectPrefab;

    // Start is called before the first frame update
    void Start()
    {
        rb = GetComponent<Rigidbody>();
        rb.AddForce(transform.forward * speed, ForceMode.Impulse);
        Destroy(gameObject, 1f);
        
    }

    private void OnCollisionEnter(Collision collision)
    {
        // 如果打到可破坏物体
        if(collision.gameObject.tag == "des")
        {
            Rigidbody rbody = collision.gameObject.GetComponent<Rigidbody>();
            if(rbody == null)
            {
                rbody = collision.gameObject.AddComponent<Rigidbody>();
            }
            rbody.AddForceAtPosition(transform.forward * 3, collision.contacts[0].point,
                ForceMode.Impulse);
            Destroy(collision.gameObject.GetComponent<Collider>(), 0.04f);
            Destroy(collision.gameObject, 2f);
        }

        // 打到敌人
        if(collision.gameObject.tag == "Enemy")
        {
            collision.gameObject.GetComponent<EnemyControl>().Gethit(2);
        }

        var go = Instantiate(effectPrefab, transform.position,
            Quaternion.LookRotation(collision.contacts[0].normal));
        Destroy(go, 1f);
        Destroy(gameObject);
    }


}
