using UnityEngine;

/* 插值远端快照，减少联机角色抖动。 */
public class NetworkPlayerView : MonoBehaviour
{
    public float positionSmoothTime = 0.08f; /* 位置平滑时间（秒）。 */
    public float rotationSharpness = 14f;
    private Vector3 targetPosition;
    private Quaternion targetRotation;
    private Vector3 velocity; /* SmoothDamp 保存的插值速度。 */
    public int CurrentHealth { get; private set; } = 100;
    public int MaxHealth { get; private set; } = 100;
    public bool IsDead { get; private set; }
    public float RespawnRemaining { get; private set; }

    public void SetTarget(Vector3 position, Quaternion rotation)
    {
        /* 接收阶段：只记录服务器目标，实际插值在 Unity 主线程 Update 中完成。 */
        targetPosition = position;
        targetRotation = rotation;
    }

    public void SetHealth(int current, int maximum, bool dead, float respawnRemaining)
    {
        /* 状态阶段：保存远端权威血量、死亡标记和复活倒计时。 */
        CurrentHealth = Mathf.Max(0, current);
        MaxHealth = Mathf.Max(1, maximum);
        IsDead = dead;
        RespawnRemaining = Mathf.Max(0f, respawnRemaining);
    }

    private void Update()
    {
        /* 表现阶段：平滑位置和朝向，避免网络快照造成跳变。 */
        transform.position = Vector3.SmoothDamp(transform.position, targetPosition, ref velocity, positionSmoothTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, 1f - Mathf.Exp(-rotationSharpness * Time.unscaledDeltaTime));
    }
}
