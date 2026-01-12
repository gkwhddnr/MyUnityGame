using UnityEngine;
// using Unity.Netcode; // 네트워크 관련 코드 제거
using System.Linq;

public class PlayerHealth2 : MonoBehaviour
{
    [Header("Health")]
    public float maxHealth = 100f;
    public float Health = 100f; // NetworkVariable 대신 일반 float 변수 사용

    [Header("Respawn / Zombie")]
    public GameObject zombiePrefab;

    [Header("Audio")]
    public AudioSource deathAudioSource;
    public AudioClip[] deathClips;

    private void Start()
    {
        Health = maxHealth;
    }

    public void ApplyDamage(float damage)
    {
        Health -= damage;
        if (Health <= 0f)
        {
            DieAndSpawnZombie();
        }
    }

    private void DieAndSpawnZombie()
    {
        // 서버에서만 실행되는 조건 제거
        Vector3 deathPos = transform.position;

        // PlayDeathSoundClientRpc() 대신 바로 사운드 재생
        if (deathAudioSource != null && deathClips != null && deathClips.Length > 0)
        {
            int idx = Random.Range(0, deathClips.Length);
            deathAudioSource.PlayOneShot(deathClips[idx]);
        }

        // 카메라 전환 로직 제거 (싱글플레이어이므로 불필요)

        if (zombiePrefab != null)
        {
            // 네트워크 관련 코드 없이 바로 좀비 생성
            var go = Instantiate(zombiePrefab, deathPos, Quaternion.identity);
        }

        // 플레이어 오브젝트는 파괴
        Destroy(gameObject);
    }

    // ClientRpc 함수들 모두 제거
}