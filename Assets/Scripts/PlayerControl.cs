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

    private float xRotation; // 俯仰角
    private float yaw; // 水平朝向
    private Vector2 moveInput; // 平面移动输入
    private bool jumpRequested;

    [HideInInspector]
    public bool highSpeed = false;
    [HideInInspector]
    public bool isAiming = false;

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        ani = GetComponentInChildren<Animator>();
        rb.interpolation = RigidbodyInterpolation.Interpolate;
        yaw = rb.rotation.eulerAngles.y;
        // 保留设置，首次运行使用预制体默认值。
        xScensitivity = PlayerPrefs.GetFloat("PlayerSensitivityX", xScensitivity);
        yScensitivity = PlayerPrefs.GetFloat("PlayerSensitivityY", yScensitivity);
        Cursor.lockState = CursorLockMode.Locked;
        // 运行时挂载血量，保留原 Player 预制体和 AudioSource。
        if (GetComponent<PlayerHealth>() == null)
            gameObject.AddComponent<PlayerHealth>();
    }

    void Update()
    {
        // 先处理死亡和菜单，避免残留移动或跳跃输入。
        PlayerHealth health = GetComponent<PlayerHealth>();
        if (health != null && health.IsDead)
        {
            moveInput = Vector2.zero;
            jumpRequested = false;
            return;
        }
        // 菜单键优先处理，避免打开菜单时鼠标晃动镜头。
        if (GameModeManager.IsGameplayPaused || GameModeManager.IsMenuVisible ||
            Input.GetKeyDown(KeyCode.Escape))
        {
            moveInput = Vector2.zero;
            jumpRequested = false;
            return;
        }
        Aim();
        // 普通游戏帧依次读取视角、冲刺和移动。
        ReadLookInput();
        HighSpeed();
        ReadMoveInput();
        jumpRequested |= Input.GetKeyDown(KeyCode.Space);
    }

    void Aim()
    {
        float aim = ani.GetFloat("Aiming");
        if (Input.GetMouseButton(1))
        {
            isAiming = true;
            ani.SetBool("Aim", true);
            ani.SetFloat("Aiming", Mathf.Lerp(aim, 1, 0.2f));
        }
        else
        {
            isAiming = false;
            ani.SetBool("Aim", false);
            ani.SetFloat("Aiming", Mathf.Lerp(aim, 0, 0.2f));
        }
    }

    void ReadLookInput()
    {
        // 每帧读取鼠标；独立保存 yaw，避免刚体插值反馈到输入。
        float x = Input.GetAxis("Mouse X");
        float y = Input.GetAxis("Mouse Y");

        // 上下视角只记录输入，在 LateUpdate 应用，避免被 Animator 覆盖
        xRotation -= y * yScensitivity;
        xRotation = Mathf.Clamp(xRotation, -80, 80);
        yaw += x * xScensitivity;
        if (yaw > 360f || yaw < -360f)
            yaw = Mathf.Repeat(yaw, 360f);
    }

    void ReadMoveInput()
    {
        moveInput.x = Input.GetAxis("Horizontal");
        moveInput.y = Input.GetAxis("Vertical");
        moveInput = Vector2.ClampMagnitude(moveInput, 1f);
        ani.SetFloat("Movement", moveInput.magnitude);
    }

    void HighSpeed()
    {
        // 只有接地时允许冲刺，同时切换持枪动画。
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

    public bool IsGround()
    {
        RaycastHit hit;
        bool res = Physics.Raycast(rb.position + Vector3.up * 0.2f,
            -Vector3.up, out hit, 0.4f, LayerMask.GetMask("Ground"));
        return res;
    }

    private void FixedUpdate()
    {
        // 物理帧再次屏蔽死亡和菜单，防止继续惯性移动。
        PlayerHealth health = GetComponent<PlayerHealth>();
        if (health != null && health.IsDead)
        {
            if (rb != null) rb.velocity = Vector3.zero;
            jumpRequested = false;
            return;
        }
        if (GameModeManager.IsGameplayPaused || GameModeManager.IsMenuVisible)
        {
            rb.velocity = Vector3.zero;
            jumpRequested = false;
            return;
        }

        Quaternion rotation = Quaternion.Euler(0f, yaw, 0f);

        // 联机由客户端预测并向服务器发输入，单机沿用刚体移动。
        NetworkClient network = NetworkClient.Active;
        if (network != null && network.DrivePlayer(rb, moveInput, rotation.eulerAngles.y,
            xRotation, jumpRequested, highSpeed))
        {
            jumpRequested = false;
            return;
        }

        // 单机平面速度与重力分开，只在接地时处理跳跃。
        rb.MoveRotation(rotation);
        Vector3 dir = rotation * new Vector3(moveInput.x, 0f, moveInput.y);
        if (dir.sqrMagnitude > 1f)
            dir.Normalize();

        Vector3 velocity = dir * speed;
        velocity.y = rb.velocity.y;
        if (jumpRequested && IsGround())
            velocity.y = jumpForce;

        jumpRequested = false;
        rb.velocity = velocity;
    }

    private void LateUpdate()
    {
        PlayerHealth health = GetComponent<PlayerHealth>();
        if (health != null && health.IsDead)
            return;
        if (ani != null)
            ani.transform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
    }
}
