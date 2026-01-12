using UnityEngine;

public class Engineer : MonoBehaviour
{
    public KeyCode droneKey = KeyCode.Z;
    private bool isControllingDrone = false;
    private GameObject drone;
    private Camera playerCamera;
    private Camera droneCamera;

    void Start()
    {
        playerCamera = Camera.main;

        // 게임 시작 시 드론 생성
        drone = new GameObject("Drone");

        if (drone.GetComponent<Drone>() == null)
        {
            drone.AddComponent<Drone>();
        }

        drone.transform.position = transform.position + new Vector3(2, 1, 0);
        drone.SetActive(false);

        droneCamera = drone.GetComponentInChildren<Camera>();
    }

    public void ResetDailyAction()
    {
        Debug.Log("엔지니어 능력 초기화 완료.");
    }

    void Update()
    {
        if (Input.GetKeyDown(droneKey))
        {
            isControllingDrone = !isControllingDrone;

            if (isControllingDrone)
            {
                if (playerCamera != null) playerCamera.enabled = false;
                if (droneCamera != null) droneCamera.enabled = true;
            }
            else
            {
                if (playerCamera != null) playerCamera.enabled = true;
                if (droneCamera != null) droneCamera.enabled = false;
            }
        }

        if (isControllingDrone && drone != null)
        {
            drone.GetComponent<Drone>().MoveDrone();
        }
    }
}