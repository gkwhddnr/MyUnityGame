using UnityEngine;

public class doctor : MonoBehaviour
{
    public float examineRange = 3f;
    public KeyCode examineKey = KeyCode.Z;
    private bool hasExamined = false;


    public void ResetDailyAction()
    {
        hasExamined = false;
        Debug.Log("의사 능력 초기화 완료. 새로운 대상을 진찰할 수 있습니다.");
    }

    void Update()
    {
        if (Input.GetKeyDown(examineKey) && !hasExamined)
        {
            UseAbility();
        }
    }

    private void UseAbility()
    {
        Collider[] hitColliders = Physics.OverlapSphere(transform.position, examineRange);

        foreach (var hitCollider in hitColliders)
        {

        }
    }
}