using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RecoilControl : MonoBehaviour
{

    public float X = -3f;
    public float speed = 10;
    public float returnSpeed = 5;
    private float targeRotation;
    private float currentRotation;

    // Update is called once per frame
    void Update()
    {
        // 恢复
        targeRotation = Mathf.Lerp(targeRotation, 0, returnSpeed * Time.deltaTime);
        // 旋转
        currentRotation = Mathf.Lerp(currentRotation, targeRotation, speed * Time.deltaTime);
        // 应用旋转
        transform.localRotation = Quaternion.Euler(currentRotation, transform.localEulerAngles.y, 0);

    }

    public void Fire()
    {
        targeRotation += X;
    }
}
