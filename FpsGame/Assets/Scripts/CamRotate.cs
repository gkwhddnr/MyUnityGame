using UnityEngine;

public class CamRotate : MonoBehaviour
{
    public float rotSpeed = 200f;

    // 회전값 변수
    float mx = 0;
    float my = 0;
    
    void Update()
    {
        float mouse_X = Input.GetAxis("Mouse X");
        float mouse_Y = Input.GetAxis("Mouse Y");

        // 회전 값 변수에 마우스 입력 값만큼 미리 누적
        mx += mouse_X * rotSpeed * Time.deltaTime;
        my += mouse_Y * rotSpeed * Time.deltaTime;

        // 마우스 상하 이동 회전 변수의 값을 -90 ~ 90도로 사이로 제한
        my = Mathf.Clamp(my, -90f, 90f);

        // 회전 방향 결정
        Vector3 dir = new Vector3 (-mouse_Y, mouse_X, 0);

        // 회전 방향으로 물체 회전    ->      r = r0 + vt
        transform.eulerAngles = new Vector3(-my, mx, 0);
        // transform.eulerAngles += dir * rotSpeed * Time.deltaTime;

        /*
        // 상하 회전 값을 -90 ~ 90도 사이로 제한
        Vector3 rot = transform.eulerAngles;
        rot.x = Mathf.Clamp(rot.x, -90f, 90f);
        transform.eulerAngles = rot;
        */
    }
}
