using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

public class PlayerSpectatorManager : NetworkBehaviour
{
    public static PlayerSpectatorManager Instance { get; private set; }

    public readonly NetworkVariable<PlayerSlotDataList> playerSlots = new NetworkVariable<PlayerSlotDataList>(
        new PlayerSlotDataList { PlayerSlots = new List<PlayerSlotData>() },
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    [Header("Audio on Player Death")]
    public AudioSource audioSource;       // Inspector에서 할당
    public AudioClip[] deathClips;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsServer)
        {
            // 이미 연결된 클라이언트들에 대해 슬롯 할당 시도
            foreach (var kv in NetworkManager.Singleton.ConnectedClients)
            {
                var clientId = kv.Key;
                var playerObj = kv.Value.PlayerObject;
                ulong playerNetId = playerObj != null ? playerObj.NetworkObjectId : 0UL;
                AssignSlotForClient(clientId, playerNetId);
            }

            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        }
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
    }

    // 서버 측: 클라 접속
    private void OnClientConnected(ulong clientId)
    {
        if (!IsServer) return;
        StartCoroutine(DelayedSlotAssignment(clientId));
    }

    private IEnumerator DelayedSlotAssignment(ulong clientId)
    {
        // PlayerObject가 바로 안붙을 수 있어 약간 대기
        yield return new WaitForSeconds(0.05f);

        if (NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client))
        {
            ulong playerNetId = client.PlayerObject != null ? client.PlayerObject.NetworkObjectId : 0UL;
            AssignSlotForClient(clientId, playerNetId);
        }
    }

    // 서버: 클라 연결 끊김
    private void OnClientDisconnected(ulong clientId)
    {
        if (!IsServer) return;

        var list = playerSlots.Value.PlayerSlots ?? new List<PlayerSlotData>();
        var found = list.FirstOrDefault(s => s.ClientId == clientId);
        if (found.SlotNumber != 0)
        {
            list.RemoveAll(s => s.ClientId == clientId || s.SlotNumber == found.SlotNumber);
            playerSlots.Value = new PlayerSlotDataList { PlayerSlots = list };

            Debug.Log($"[Spectator] Client {clientId} disconnected. Freed slot {found.SlotNumber}.");
        }
    }

    // 서버 전용: 슬롯 할당 / 갱신
    public void AssignSlotForClient(ulong clientId, ulong playerNetId)
    {
        if (!IsServer) return;

        var list = playerSlots.Value.PlayerSlots ?? new List<PlayerSlotData>();

        // 이미 슬롯 가지고 있으면 playerNetId만 갱신
        var existing = list.FirstOrDefault(s => s.ClientId == clientId);
        if (existing.SlotNumber != 0)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].ClientId == clientId)
                {
                    var updated = list[i];
                    updated.PlayerNetId = playerNetId;
                    list[i] = updated;
                    playerSlots.Value = new PlayerSlotDataList { PlayerSlots = list };
                    Debug.Log($"[Spectator] Updated slot {updated.SlotNumber} for client {clientId} with playerNetId {playerNetId}");
                    var clientParams = new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
                    };
                    ShowAllNameplatesClientRpc(false, clientParams);
                    return;
                }
            }
        }

        // 빈 슬롯 찾기
        for (int i = 1; i <= 8; i++)
        {
            if (!list.Any(s => s.SlotNumber == i))
            {
                var newSlot = new PlayerSlotData
                {
                    SlotNumber = i,
                    ClientId = clientId,
                    PlayerNetId = playerNetId
                };
                list.Add(newSlot);
                playerSlots.Value = new PlayerSlotDataList { PlayerSlots = list };
                Debug.Log($"[Spectator] Assigned slot {i} to client {clientId} (playerNetId {playerNetId})");
                var clientParams = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
                };
                ShowAllNameplatesClientRpc(false, clientParams);
                return;
            }
        }
    }

    // 서버: 플레이어 사망 처리 (PlayerHealth에서 호출)
    public void Server_HandlePlayerDeath(ulong deadClientId)
    {
        if (!IsServer) return;

        var list = playerSlots.Value.PlayerSlots ?? new List<PlayerSlotData>();
        var found = list.FirstOrDefault(s => s.ClientId == deadClientId);
        if (found.SlotNumber == 0) return;

        int slot = found.SlotNumber;
        list.RemoveAll(s => s.ClientId == deadClientId);
        playerSlots.Value = new PlayerSlotDataList { PlayerSlots = list };

        Debug.Log($"[Spectator] Player {deadClientId} (slot {slot}) died => slot cleared.");

        // 모든 클라이언트에게 알림 (선택적)
        NotifySpectatorCameraClientRpc(slot);
        // 2) 사망 효과음 재생 RPC
        if (PlayerHealth.Instance.Health.Value == 0) PlayDeathSoundClientRpc();

        var clientParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { (ulong)deadClientId } }
        };
        // 해당 클라이언트에서 Spectator 카메라로 제어권을 바로 넘기도록 호출
        TakeControlClientRpc(true, clientParams);
        ShowAllNameplatesClientRpc(true, clientParams);
    }

    [ClientRpc]
    private void TakeControlClientRpc(bool freeLook = true, ClientRpcParams rpcParams = default)
    {
        if (SpectatorCamera.Instance == null) return;
        
        Transform camTransform = Camera.main != null ? Camera.main.transform : null;
        SpectatorCamera.Instance.TakeControl(camTransform);
        SpectatorCamera.Instance.SetFreeLookMode(freeLook);
    }

    [ClientRpc]
    public void FollowByNetworkIdClientRpc(ulong targetNetworkId, int slotNumber = 0, ClientRpcParams rpcParams = default)
    {
        if (SpectatorCamera.Instance == null) return;
        
        if (NetworkManager.Singleton == null) return;
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(targetNetworkId, out var netObj))
        {
            Transform camTransform = Camera.main != null ? Camera.main.transform : null;
            SpectatorCamera.Instance.TakeControl(camTransform);
            SpectatorCamera.Instance.SetFollowTarget(netObj.transform, slotNumber);
        }
    }

    [ClientRpc]
    private void PlayDeathSoundClientRpc(ClientRpcParams rpcParams = default)
    {
        if (audioSource != null && deathClips != null && deathClips.Length > 0)
        {
            int idx = Random.Range(0, deathClips.Length);
            audioSource.PlayOneShot(deathClips[idx]);
        }
    }


    [ClientRpc]
    private void NotifySpectatorCameraClientRpc(int deadSlotNumber)
    {
        if (SpectatorCamera.Instance != null)
        {
            SpectatorCamera.Instance.OnPlayerDeath(deadSlotNumber);
        }
    }

    [ClientRpc]
    private void ShowAllNameplatesClientRpc(bool show, ClientRpcParams rpcParams = default)
    {
        // 클라이언트에서만 동작
        if (IsServer) return;

#if UNITY_2023_2_OR_NEWER
        var all = UnityEngine.Object.FindObjectsByType<PlayerMovement>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        var all = Resources.FindObjectsOfTypeAll<PlayerMovement>();
#endif
        foreach (var pm in all)
        {
            if (pm == null) continue;
            // 씬 아닌 에셋 등 제외
            if (pm.gameObject.scene.IsValid() == false) continue;

            var text = pm.nameText;
            if (text == null) continue;

            bool shouldShow = show;
            try
            {
                // 살아있는 플레이어만 표시
                shouldShow = show && !pm.isDead.Value;
            }
            catch
            {
                // 실패시 기존 동작 유지
            }

            try
            {
                text.gameObject.SetActive(shouldShow);
            }
            catch { }
        }
    }

    // 클라용: 슬롯->NetworkObject 반환
    public NetworkObject GetPlayerBySlot(int slot)
    {
        var list = playerSlots.Value.PlayerSlots;
        if (list == null) return null;

        var slotData = list.FirstOrDefault(s => s.SlotNumber == slot);
        if (slotData.SlotNumber == 0) return null;

        // PlayerNetId로 SpawnedObjects에서 찾아 반환
        if (slotData.PlayerNetId != 0UL && NetworkManager.Singleton != null)
        {
            if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(slotData.PlayerNetId, out var netObj))
            {
                return netObj;
            }
        }

        return null;
    }

    public void OnLocalPlayerDeath()
    {
        if (SpectatorCamera.Instance == null) return;
        
        Transform camTransform = Camera.main != null ? Camera.main.transform : null;
        SpectatorCamera.Instance.TakeControl(camTransform);
        SpectatorCamera.Instance.SetFreeLookMode(true);
    }

    public void RefreshClientsVisionOnAllClients()
    {
        if (!IsServer) return;
        // broadcast client RPC
        RefreshClientsVisionClientRpc();
    }

    [ClientRpc]
    private void RefreshClientsVisionClientRpc(ClientRpcParams rpcParams = default)
    {
        VisionController[] vcs;

#if UNITY_2023_2_OR_NEWER
        // 2023.2+ : FindObjectsByType 사용 (inactive 포함, 정렬 없음)
        vcs = UnityEngine.Object.FindObjectsByType<VisionController>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );
#else
    // 이전 버전: Resources.FindObjectsOfTypeAll 사용 후, 씬에 로드된 인스턴스만 필터링
    var all = Resources.FindObjectsOfTypeAll<VisionController>();
    vcs = all.Where(vc =>
    {
        return vc != null && vc.gameObject != null && vc.gameObject.scene.IsValid() && vc.gameObject.scene.isLoaded;
    }).ToArray();
#endif

        foreach (var vc in vcs)
        {
            if (vc == null) continue;
            // VisionController 쪽에 public으로 구현했다고 가정한 메서드
            // (없다면 VisionController에 아래 메서드 구현 필요: public void RefreshLitObjectsCache() { ... })
            vc.RefreshLitObjectsCache();
        }
    }

}
