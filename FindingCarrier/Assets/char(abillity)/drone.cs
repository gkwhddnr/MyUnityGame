using UnityEngine;

public class Drone : MonoBehaviour
{
    public float moveSpeed = 5f;
    public float detectRange = 10f;

    void Update()
    {
        // 주변 캐릭터를 감지합니다.
        DetectNearbyPlayers();
    }

    public void MoveDrone()
    {
        float horizontal = Input.GetAxis("Horizontal");
        float vertical = Input.GetAxis("Vertical");

        Vector3 direction = new Vector3(horizontal, 0, vertical);
        transform.Translate(direction * moveSpeed * Time.deltaTime, Space.World);
    }

    private void DetectNearbyPlayers()
    {
        Collider[] hitColliders = Physics.OverlapSphere(transform.position, detectRange);
        foreach (var hitCollider in hitColliders)
        {

        }
    }
}