using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RecoilControl : MonoBehaviour
{
    public float X = -3f;
    public float speed = 10;
    public float returnSpeed = 5;
    private float targeRotation; // 连发累积的目标后坐力角度。
    private float currentRotation; // 当前平滑角度。
    private Transform recoilTarget;
    private Quaternion baseRotation;

    private void Awake()
    {
        Camera camera = GetComponentInChildren<Camera>(true);
        if (camera != null)
        {
            recoilTarget = camera.transform;
            baseRotation = recoilTarget.localRotation;
        }
        else
        {
            Debug.LogWarning("RecoilControl requires a child Camera.", this);
            enabled = false;
        }
    }

    private void LateUpdate()
    {
        // 恢复
        targeRotation = Mathf.Lerp(targeRotation, 0, returnSpeed * Time.deltaTime);
        // 旋转
        currentRotation = Mathf.Lerp(currentRotation, targeRotation, speed * Time.deltaTime);
        // 后坐力只作用于摄像机，避免覆盖玩家 Rigidbody 的旋转
        recoilTarget.localRotation = baseRotation * Quaternion.Euler(currentRotation, 0f, 0f);
    }

    public void Fire()
    {
        targeRotation += X;
    }
}
