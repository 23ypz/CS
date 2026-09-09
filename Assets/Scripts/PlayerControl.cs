using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class PlayerControl : MonoBehaviour
{
    private Rigidbody rb;
    private Animator ani;

    // 速度
    public float speed = 3f;
    public float jumpForce = 5;
    // 灵敏度
    public float xScensitivity = 7;
    public float yScensitivity = 7;

    private float xRotation = 0;
    private Vector3 velocity;
    private bool jump = false;

    [HideInInspector]
    public bool highSpeed = false;
    [HideInInspector]
    public bool isAiming = false;

    private float mouseX;

    // Start is called before the first frame update
    void Start()
    {
        rb = GetComponent<Rigidbody>();
        ani = GetComponentInChildren<Animator>();
        Cursor.lockState = CursorLockMode.Locked;
    }

    // Update is called once per frame
    void Update()
    {
        Aim();
        Mouse();
        HighSpeed();
        Move();
        Jump();

        //Debug.DrawRay(transform.position + Vector3.up * 0.2f,
        //    -Vector3.up * 0.4f, Color.red);
    }

    void Aim()
    {
        float aim = ani.GetFloat("Aiming");
        if (Input.GetMouseButton(1))
        {
            isAiming = true;
            ani.SetBool("Aim", true);
            ani.SetFloat("Aiming", Mathf.Lerp(aim,1,0.2f));
        }
        else
        {
            isAiming = false;
            ani.SetBool("Aim", false);
            ani.SetFloat("Aiming", Mathf.Lerp(aim, 0, 0.2f));
        }
    }

    void Mouse()
    {
        float x = Input.GetAxis("Mouse X");
        float y = Input.GetAxis("Mouse Y");

        // 上下旋转
        xRotation -= y * yScensitivity;
        xRotation = Mathf.Clamp(xRotation, -80, 80);
        ani.transform.localRotation = Quaternion.Euler(xRotation, 0, 0);
        // 左右旋转
        transform.Rotate(Vector3.up * x * xScensitivity);
    }

    void Move()
    {
        // 获取水平输入 -1 0 1
        float horizontal = Input.GetAxis("Horizontal");
        // 获取垂直输入 
        float vertical = Input.GetAxis("Vertical");
        // 创建向量 当前角色移动的方向
        Vector3 dir = (transform.forward * vertical + transform.right * horizontal).normalized;
        // 速度
        velocity = dir * speed;
        velocity.y = rb.velocity.y;
        // 移动动画
        ani.SetFloat("Movement", dir.magnitude);
    }

    void HighSpeed()
    {
        if (Input.GetKey(KeyCode.LeftShift) && IsGround())
        {
            highSpeed = true;
            speed = 5;
            ani.SetBool("Holstered", true);
        }
        else
        {
            highSpeed = false;
            speed = 3;
            ani.SetBool("Holstered", false);
        }
    }

    void Jump()
    {
        if(Input.GetKeyDown(KeyCode.Space) && IsGround())
        {
            jump = true;
        }
    }

    public bool IsGround()
    {
        RaycastHit hit;
        bool res = Physics.Raycast(transform.position + Vector3.up * 0.2f,
            -Vector3.up, out hit, 0.4f, LayerMask.GetMask("Ground"));
        return res;
    }

    private void FixedUpdate()
    {
        if (jump)
        {
            jump = false;
            velocity.y = jumpForce;
        }
        rb.velocity = velocity;
    }
}
