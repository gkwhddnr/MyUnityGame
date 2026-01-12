using UnityEngine;

public class Pharmacist : MonoBehaviour
{
    public float immunityRange = 3f;
    public float immunityDuration = 60f;
    public KeyCode immunityKey = KeyCode.Z;
    private bool canImmunize = true;

    public void ResetDailyAction()
    {
        canImmunize = true;
        Debug.Log("약사 능력 초기화 완료.");
    }

    void Update()
    {
        if (Input.GetKeyDown(immunityKey) && canImmunize)
        {
            UseAbility();
        }
    }

    private void UseAbility()
    {
        Collider[] hitColliders = Physics.OverlapSphere(transform.position, immunityRange);
        foreach (var hitCollider in hitColliders)
        {

        }
    }
}