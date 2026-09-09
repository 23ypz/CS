using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class WeaponControl : MonoBehaviour
{
    // 发射位置
    public GameObject FirePoint;
    // 子弹
    public GameObject BulletPre;
    // 火焰效果
    public GameObject FirePre;
    // 时间间隔
    public float bulletInterval = 0.3f;
    private float timer = 0;
    private PlayerControl pc;
    private RecoilControl rc;
    private AudioSource AS;
    public AudioClip fireSound;



    // Start is called before the first frame update
    void Start()
    {
        pc = GetComponent<PlayerControl>();
        rc = GetComponent<RecoilControl>();
        AS = GetComponent<AudioSource>();

    }

    // Update is called once per frame
    void Update()
    {
        timer += Time.deltaTime;
        if(Input.GetMouseButton(0) && timer >= bulletInterval && !pc.highSpeed)
        {
            timer = 0;
            // 后坐力
            rc.Fire();
            // 创建子弹
            Instantiate(BulletPre, FirePoint.transform.position, FirePoint.transform.rotation);
            // 测试声音
            AS.PlayOneShot(fireSound);
            // 显示效果
            Destroy(Instantiate(FirePre, FirePoint.transform.position, FirePoint.transform.rotation),
                0.1f);
        }
    }
}
