using UnityEngine;

public class Villain : MonoBehaviour
{
    public float arrestRange = 3f;
    public KeyCode arrestKey = KeyCode.Z;
    private bool canArrest = true;

    public void ResetDailyAction()
    {
        canArrest = true;
        Debug.Log("빌런 능력 초기화 완료.");
    }

    void Update()
    {
        if (Input.GetKeyDown(arrestKey) && canArrest)
        {
            UseAbility();
        }
    }

    private void UseAbility()
    {
        Collider[] hitColliders = Physics.OverlapSphere(transform.position, arrestRange);
        foreach (var hitCollider in hitColliders)
        {

        }
    }
}