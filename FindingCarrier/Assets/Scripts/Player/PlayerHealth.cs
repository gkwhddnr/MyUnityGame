using UnityEngine;
using Unity.Netcode;
using System.Linq;
using System.Collections;

public class PlayerHealth : NetworkBehaviour
{
    public static PlayerHealth Instance;

    [Header("Health")]
    public float maxHealth = 100f;
    public NetworkVariable<float> Health = new NetworkVariable<float>(
        100f,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    [Header("Respawn / Zombie")]
    public GameObject zombiePrefab;
    public GameObject deathEffectPrefab; // 인스펙터에 파티클 프리팹 할당
    public AudioClip deathSound;

    public bool suppressZombieSpawn = false;

    private void Start()
    {
        if (IsServer)
        {
            Health.Value = maxHealth;
        }
        Instance = this;
        // 슬롯 등록은 서버가 NetworkManager callbacks로 처리하므로 여기서 시도하지 않음.
    }

    public void ApplyDamage(float damage)
    {
        if (!IsServer) return;

        Health.Value -= damage;
        if (Health.Value <= 0f)
        {
            DieAndSpawnZombie();
        }
    }

    private void DieAndSpawnZombie()
    {
        if (!IsServer) return;

        Vector3 deathPos = transform.position;
        var zombies = ZombieController.GetServerZombies();
        if (zombies != null)
        {
            foreach (var z in zombies)
            {
                if (z == null || !z.IsServer) continue;
                if (z.HasTarget(transform)) z.ClearTargetAndResume();
            }
        }

        var pcm = FindFirstObjectByType<PlayerCharacterManager>();
        if (pcm != null)
        {
            pcm.DestroyContainerWithCharacter(gameObject);
        }

        // 클라이언트의 카메라 전환을 먼저 알림 (로컬 카메라 제어 로직은 클라이언트 쪽에서 처리)
        NotifyDeathClientRpc();
        StartCoroutine(DespawnAfterDelay(0.1f));

        // 서버 측에서 슬롯/관전 데이터 정리
        if (PlayerSpectatorManager.Instance != null)
        {
            PlayerSpectatorManager.Instance.Server_HandlePlayerDeath(OwnerClientId);
        }

        if (GlobalNotificationManager.Instance != null)
        {
            string msg = "<color=yellow>누군가가</color> <color=red>감염자</color>에게 습격당해 <color=red>사망</color>하였습니다.";
            GlobalNotificationManager.Instance.ShowGlobalMessageClientRpc(msg);
        }

        if (zombiePrefab != null && !suppressZombieSpawn)
        {
            var go = Instantiate(zombiePrefab, deathPos, Quaternion.identity);
            var zNet = go.GetComponent<NetworkObject>();
            if (zNet != null)
            {
                zNet.Spawn();
                PlayerSpectatorManager.Instance?.RefreshClientsVisionOnAllClients();
            }
            else
            {
                Destroy(go);
            }
        }

        if (NetworkObject != null && NetworkObject.IsSpawned)
        {
            // 플레이어 오브젝트는 소멸(게임 규칙에 따라)
            NetworkObject.Despawn(destroy: true);
        }
    }

    private IEnumerator DespawnAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (NetworkObject != null && NetworkObject.IsSpawned)
        {
            NetworkObject.Despawn(true);
        }
    }


    [ClientRpc]
    public void NotifyDeathClientRpc()
    {
        // 로컬 플레이어(죽은 플레이어)에서 카메라 제어를 넘기는 역할
        var localCameraFollow = GetComponent<CameraFollow>();
        if (localCameraFollow != null)
        {
            localCameraFollow.OnPlayerDeath();
        }

        if (IsOwner)
        {
            if(PersonalNotificationManager.Instance != null)
            {
                var personalUI = FindFirstObjectByType<PersonalNotificationManager>(FindObjectsInactive.Include);
                if (personalUI != null)
                {
                    string deathMessage = "당신은 <color=red>죽었습니다. </color>\n 관전하고 싶은 <color=green>플레이어</color>가 있다면\n <color=cyan>번호키</color>를 눌러주세요.";
                    personalUI.PersistentShowPersonalMessage(deathMessage);
                }
            }

            var deathPersonalUI = FindFirstObjectByType<DeathNotificationUI>();
            deathPersonalUI?.ShowDeathNotification();
        }
    }

    // 캐릭터 오브젝트 Health 같은 곳
    public void Die()
    {
        if (TryGetComponent<NetworkObject>(out var netObj))
        {
            if (netObj.IsSpawned)
                netObj.Despawn(true);
            else
                Destroy(gameObject);
        }

        // 컨테이너도 제거
        var manager = FindFirstObjectByType<PlayerCharacterManager>();
        if (manager != null && manager.spawnedCharacters != null)
        {
            // PlayerContainer의 NetId 찾기
            ulong playerNetId = 0;
            foreach (var kv in manager.spawnedCharacters)
            {
                if (kv.Value == this.gameObject) // 현재 죽은 캐릭터
                {
                    playerNetId = kv.Key; // key = container의 NetId
                    break;
                }
            }

            if (playerNetId != 0 &&
                NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(playerNetId, out var containerNetObj))
            {
                if (containerNetObj.IsSpawned)
                    containerNetObj.Despawn(true);
                else
                    Destroy(containerNetObj.gameObject);
            }
        }
    }

    public void ConvertToZombieServer(GameObject zombieNetworkPrefab)
    {
        if (!IsServer) return;

        if (suppressZombieSpawn) return;
        suppressZombieSpawn = true;

        Vector3 deathPos = transform.position;
        Quaternion deathRot = transform.rotation;

        try
        {
            NotifyDeathClientRpc();
            GlobalNotificationManager.Instance.ShowGlobalMessageClientRpc("누군가가 보균자의 감염에 의해 사망하였습니다.");
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"[PlayerHealth] NotifyDeathClientRpc failed: {ex}");
        }

        if (PlayerSpectatorManager.Instance != null)
        {
            try
            {
                PlayerSpectatorManager.Instance.Server_HandlePlayerDeath(OwnerClientId);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"[PlayerHealth] Server_HandlePlayerDeath call failed: {ex}");
            }
        }

        // 2) 모든 클라이언트에 이펙트/사운드 재생 지시 (위치 전달)
        PlayDeathEffectClientRpc(deathPos);

        // 3) 좀비 프리팹 스폰 (서버에서만)
        if (zombieNetworkPrefab != null)
        {
            var go = Instantiate(zombieNetworkPrefab, deathPos, deathRot);
            var netObj = go.GetComponent<NetworkObject>();
            if (netObj != null) netObj.Spawn();
            else Destroy(go);
        }

        if (NetworkObject != null && NetworkObject.IsSpawned) NetworkObject.Despawn(destroy: true);
        else Destroy(gameObject);
    }


    // ClientRpc: 모든 클라이언트에서 이펙트/사운드를 재생
    [ClientRpc]
    private void PlayDeathEffectClientRpc(Vector3 pos, ClientRpcParams rpcParams = default)
    {
        if (deathEffectPrefab != null)
        {
            var fx = Instantiate(deathEffectPrefab, pos, Quaternion.identity);
            var ps = fx.GetComponentInChildren<ParticleSystem>();
            if (ps != null)
            {
                Destroy(fx, ps.main.duration + ps.main.startLifetime.constantMax + 0.1f);
            }
            else Destroy(fx, 3f);
        }

        if (deathSound != null)
        {
            // 간단하게 모두가 소리를 들음. (로컬 사운드 시스템에 맞게 변경 가능)
            AudioSource.PlayClipAtPoint(deathSound, pos);
        }
    }
}
