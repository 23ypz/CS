using UnityEngine;

/// <summary>Interpolates remote snapshots so network players do not jitter.</summary>
public class NetworkPlayerView : MonoBehaviour
{
    public float positionSmoothTime = 0.08f;
    public float rotationSharpness = 14f;
    private Vector3 targetPosition;
    private Quaternion targetRotation;
    private Vector3 velocity;

    public void SetTarget(Vector3 position, Quaternion rotation)
    {
        targetPosition = position;
        targetRotation = rotation;
    }

    private void Update()
    {
        transform.position = Vector3.SmoothDamp(transform.position, targetPosition, ref velocity, positionSmoothTime);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, 1f - Mathf.Exp(-rotationSharpness * Time.unscaledDeltaTime));
    }
}
