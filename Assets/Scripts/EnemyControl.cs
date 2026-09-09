using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EnemyControl : MonoBehaviour
{
    public int hp = 10;
    public GameObject bombEffect;

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    public void Gethit(int damage)
    {
        hp -= damage;
        if(hp <= 0)
        {
            // ±¬Õ¨
            Instantiate(bombEffect, transform.position, transform.rotation);
            Destroy(gameObject);
        }
    }
}
