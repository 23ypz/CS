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

    private float xRotation;
    private float yaw;
    private Vector2 moveInput;
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
        Cursor.lockState = CursorLockMode.Locked;
    }

    // Update is called once per frame
    void Update()
    {
        if (GameModeManager.IsGameplayPaused)
            return;
        Aim();
        ReadLookInput();
        HighSpeed();
        ReadMoveInput();
        jumpRequested |= Input.GetKeyDown(KeyCode.Space);

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

    void ReadLookInput()
    {
        // Mouse deltas are sampled directly once per rendered frame. Keeping
        // yaw separate from the Rigidbody prevents interpolation/correction
        // from feeding back into the next mouse update.
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
        if (GameModeManager.IsGameplayPaused)
        {
            rb.velocity = Vector3.zero;
            jumpRequested = false;
            return;
        }

        Quaternion nextRotation = Quaternion.Euler(0f, yaw, 0f);

        // In multiplayer the network client predicts local movement and sends
        // inputs to the authoritative Python server. The original Rigidbody
        // movement below remains untouched for single-player mode.
        NetworkClient network = NetworkClient.Active;
        if (network != null && network.DrivePlayer(rb, moveInput, nextRotation.eulerAngles.y,
            xRotation, jumpRequested, highSpeed))
        {
            jumpRequested = false;
            return;
        }

        rb.MoveRotation(nextRotation);

        Vector3 direction = nextRotation * new Vector3(moveInput.x, 0f, moveInput.y);
        if (direction.sqrMagnitude > 1f)
            direction.Normalize();

        Vector3 nextVelocity = direction * speed;
        nextVelocity.y = rb.velocity.y;
        if (jumpRequested && IsGround())
            nextVelocity.y = jumpForce;

        jumpRequested = false;
        rb.velocity = nextVelocity;
    }

    private void LateUpdate()
    {
        if (ani != null)
            ani.transform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
    }
}
