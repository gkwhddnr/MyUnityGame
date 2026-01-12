using UnityEngine;

public class Scientist : MonoBehaviour
{
    public float cureRange = 3f;
    public KeyCode cureKey = KeyCode.Z;
    private bool hasCured = false;

    public void ResetDailyAction()
    {
        hasCured = false;
        Debug.Log("과학자 능력 초기화 완료. 새로운 대상을 치료할 수 있습니다.");
    }

    void Update()
    {
        if (Input.GetKeyDown(cureKey) && !hasCured)
        {
            UseAbility();
        }
    }

    private void UseAbility()
    {
        Collider[] hitColliders = Physics.OverlapSphere(transform.position, cureRange);

        foreach (var hitCollider in hitColliders)
        {

        }
    }
}