using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Head : MonoBehaviour
{
    private Transform head;
    private Transform body;
    private Rigidbody rigidbody;
    void Start()
    {
        head = transform;
        body = transform.parent;
        rigidbody = GetComponent<Rigidbody>();
        Cursor.lockState = CursorLockMode.Locked;
    }

    void Update()
    {
        // 平面移动先应用到身体，再分别读取水平与垂直鼠标。
        float x = Input.GetAxis("Horizontal");
        float z = Input.GetAxis("Vertical");
        Vector3 dir = new Vector3(x, 0, z);
        if (dir != Vector3.zero)
        {
            body.Translate(dir * Time.deltaTime * 3);
        }
        float mousex = Input.GetAxis("Mouse X");
        if (mousex != 0)
        {
            body.Rotate(Vector3.up, mousex * 120 * Time.deltaTime);
        }
        float mousey = Input.GetAxis("Mouse Y");
        if (mousey != 0)
        {
            head.Rotate(Vector3.left, mousey * 120 * Time.deltaTime);
        }
        if (Vector3.Angle(body.forward, head.forward) > 60)
        {
            // 头部与身体夹角过大时撤回本帧垂直旋转。
            head.Rotate(Vector3.left, -mousey * 120 * Time.deltaTime);
        }
    }
}
