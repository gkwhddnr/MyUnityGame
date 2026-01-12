using UnityEngine;

public class soldier : MonoBehaviour
{
    public float attackRange = 10f;
    public int damageAmount = 10;
    public KeyCode attackKey = KeyCode.Z;
    private bool isReadyToShoot = true;

    public float arrestRange = 3f;
    public KeyCode arrestKey = KeyCode.C;
    private bool hasUsedArrest = false;

    public void ResetDailyAction()
    {
        isReadyToShoot = true;
        hasUsedArrest = false;
        Debug.Log("군인 능력 초기화 완료.");
    }

    void Update()
    {
        if (Input.GetKeyDown(attackKey) && isReadyToShoot)
        {
            AttackZombie();
        }

        if (Input.GetKeyDown(arrestKey) && !hasUsedArrest)
        {
            ArrestPlayer();
        }
    }

    private void AttackZombie()
    {
        Collider[] hitColliders = Physics.OverlapSphere(transform.position, attackRange);

        foreach (var hitCollider in hitColliders)
        {
            if (hitCollider.CompareTag("Zombie"))
            {
                Debug.Log("군인이 좀비를 공격했습니다.");
                isReadyToShoot = false;
                break;
            }
        }
    }

    private void ArrestPlayer()
    {
        Collider[] hitColliders = Physics.OverlapSphere(transform.position, arrestRange);

        foreach (var hitCollider in hitColliders)
        {

        }
    }
}