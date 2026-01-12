using UnityEngine;

public class MouseLook : MonoBehaviour
{
    public float mouseSensitivity = 100f;
    public Transform cameraTransform;

    private float xRotation = 0f;
    private float yRotation = 0f;

    void Start()
    {
        // === 마우스 커서를 숨기고 고정합니다. ===
        Cursor.lockState = CursorLockMode.Locked;
        Cursor.visible = false;
    }

    void Update()
    {
        float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity * Time.deltaTime;
        float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity * Time.deltaTime;

        yRotation += mouseX;
        xRotation -= mouseY;

        xRotation = Mathf.Clamp(xRotation, -90f, 90f);

        // 이 스크립트가 붙은 부모 오브젝트를 좌우로 회전시킵니다.
        transform.localRotation = Quaternion.Euler(0f, yRotation, 0f);

        // 카메라(자식 오브젝트)를 상하로 회전시킵니다.
        if (cameraTransform != null)
        {
            cameraTransform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        }
    }
}