using UnityEngine;

public class CameraController : MonoBehaviour
{
    public Transform target; // 카메라가 따라갈 캐릭터 (인스펙터에서 연결)
    public float smoothSpeed = 0.125f; // 카메라 이동 속도
    public Vector3 offset; // 캐릭터로부터 떨어진 거리

    void LateUpdate()
    {
        if (target == null) return;

        Vector3 desiredPosition = target.position + offset;
        Vector3 smoothedPosition = Vector3.Lerp(transform.position, desiredPosition, smoothSpeed);
        transform.position = smoothedPosition;

        transform.LookAt(target);
    }
}