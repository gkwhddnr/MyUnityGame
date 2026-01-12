using UnityEngine;

public class Nurse : MonoBehaviour
{
    public float protectRange = 3f;
    public float protectDuration = 60f;
    public KeyCode protectKey = KeyCode.Z;
    private bool canProtect = true;

    public void ResetDailyAction()
    {
        canProtect = true;
        Debug.Log("간호사 능력 초기화 완료.");
    }

    void Update()
    {
        if (Input.GetKeyDown(protectKey) && canProtect)
        {
            UseAbility();
        }
    }

    private void UseAbility()
    {
        Collider[] hitColliders = Physics.OverlapSphere(transform.position, protectRange);
        foreach (var hitCollider in hitColliders)
        {

        }
    }
}