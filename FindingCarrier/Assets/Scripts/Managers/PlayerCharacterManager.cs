using System.Collections;
using System.Collections.Generic;
using System.Linq; // .ToList()를 사용하기 위해 추가
using Unity.Netcode;
using UnityEngine;

public class PlayerCharacterManager : NetworkBehaviour
{
    public GameObject[] characterPrefabs;
    public NetworkVariable<PlayerSlotDataList> slotDataList = new NetworkVariable<PlayerSlotDataList>(
        new PlayerSlotDataList { PlayerSlots = new List<PlayerSlotData>() },
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server);

    public Dictionary<ulong, GameObject> spawnedCharacters = new Dictionary<ulong, GameObject>();
    private Dictionary<ulong, GameObject> containerReferences = new Dictionary<ulong, GameObject>();
    private HashSet<ulong> pendingInstantiation = new HashSet<ulong>();

    public delegate void CharacterSpawnedHandler(GameObject character);
    public event CharacterSpawnedHandler OnCharacterSpawned;

    // 슬롯 할당을 위한 정적 변수
    private static Dictionary<ulong, int> slotAssignments = new Dictionary<ulong, int>();

    private void Awake()
    {
        slotDataList.OnValueChanged += OnSlotDataListChanged;
    }

    public override void OnDestroy()
    {
        slotDataList.OnValueChanged -= OnSlotDataListChanged;

        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnectedServer;
        }
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        slotDataList.OnValueChanged += OnSlotDataListChanged;
        // 강제 초기 호출(클라이언트 시작시 현재 값 반영)
        OnSlotDataListChanged(new PlayerSlotDataList(), slotDataList.Value);
        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnectedServer;
        }
    }

    private int GetOrAssignSlotIndex(ulong clientId)
    {
        if (!IsServer) return -1;

        // 이미 해당 클라이언트에 할당된 슬롯이 있으면 그대로 반환
        if (slotAssignments.TryGetValue(clientId, out int existing))
        {
            return existing;
        }

        // 현재 slotDataList에 이미 점유된 슬롯(서버의 최신 상태)
        var currentlyOccupied = new HashSet<int>(slotDataList.Value.PlayerSlots.Select(s => s.SlotNumber));

        // 그리고 서버가 예약해둔(다른 클라이언트에 할당된) 슬롯들도 포함
        foreach (var kv in slotAssignments)
            currentlyOccupied.Add(kv.Value);

        // 가장 낮은 인덱스부터 찾아서 할당
        for (int i = 0; i < characterPrefabs.Length; i++)
        {
            if (!currentlyOccupied.Contains(i))
            {
                slotAssignments[clientId] = i;
                Debug.Log($"[PCM] Assigned slot {i} to client {clientId}");
                return i;
            }
        }

        // 모두 찼으면 fallback: 할당 실패 (로그 남김)
        Debug.LogWarning("[PCM] No available slots to assign");
        return -1;
    }

    private void ReleaseSlotForClient(ulong clientId)
    {
        if (!IsServer) return;
        if (slotAssignments.ContainsKey(clientId))
        {
            int freed = slotAssignments[clientId];
            slotAssignments.Remove(clientId);
            Debug.Log($"[PCM] Released slot {freed} for client {clientId}");
        }
    }

    private void OnClientDisconnectedServer(ulong clientId)
    {
        // 클라이언트 id 기준으로 슬롯 해제
        ReleaseSlotForClient(clientId);

        // 또한 slotDataList 내에 남아있는 (있다면) entry는 RemovePlayer(...) 호출 흐름에서 처리되어야 함.
        Debug.Log($"[PCM] OnClientDisconnectedServer: clientId={clientId}");
    }

    public void UpdateSlots(List<PlayerSlotData> newSlots)
    {
        if (!IsServer) return;
        slotDataList.Value = new PlayerSlotDataList { PlayerSlots = newSlots };
    }

    private void OnSlotDataListChanged(PlayerSlotDataList oldList, PlayerSlotDataList newList)
    {
        if (newList.PlayerSlots == null) return;
        if (oldList.PlayerSlots == null) oldList.PlayerSlots = new List<PlayerSlotData>();

        // 나간 플레이어의 캐릭터를 제거하고 슬롯을 해제
        foreach (var oldSlot in oldList.PlayerSlots)
        {
            bool stillExists = newList.PlayerSlots.Exists(s => s.PlayerNetId == oldSlot.PlayerNetId);
            if (!stillExists)
            {
                // 캐릭터와 컨테이너 모두 정리
                if (spawnedCharacters.TryGetValue(oldSlot.PlayerNetId, out var characterGO))
                {
                    if (characterGO != null)
                    {
                        var netObj = characterGO.GetComponent<NetworkObject>();
                        if (netObj != null && netObj.IsSpawned)
                        {
                            netObj.Despawn(true);
                        }
                        else
                        {
                            Destroy(characterGO);
                        }
                    }
                    spawnedCharacters.Remove(oldSlot.PlayerNetId);
                }

                if (containerReferences.TryGetValue(oldSlot.PlayerNetId, out var containerGO))
                {
                    if (containerGO != null)
                    {
                        var netObj = containerGO.GetComponent<NetworkObject>();
                        if (netObj != null && netObj.IsSpawned)
                        {
                            netObj.Despawn(true);
                        }
                        else
                        {
                            Destroy(containerGO);
                        }
                    }
                    containerReferences.Remove(oldSlot.PlayerNetId);
                }

                // 나간 플레이어의 슬롯 정보를 올바르게 해제합니다.
                if (IsServer)
                {
                    ReleaseSlotForClient(oldSlot.ClientId);
                }
            }
        }

        if (!IsServer) return;

        // 새 슬롯 인스턴스화
        foreach (var slotData in newList.PlayerSlots)
        {
            if (spawnedCharacters.ContainsKey(slotData.PlayerNetId)) continue;
            if (pendingInstantiation.Contains(slotData.PlayerNetId)) continue;

            // 슬롯 유효성 체크
            if (slotData.SlotNumber < 0 || slotData.SlotNumber >= characterPrefabs.Length)
            {
                Debug.LogWarning($"[PlayerCharacterManager] Invalid slot index {slotData.SlotNumber} for player {slotData.PlayerNetId}");
                continue;
            }

            StartCoroutine(InstantiateWhenContainerAvailable(slotData, 5f));
        }
    }

    private IEnumerator InstantiateWhenContainerAvailable(PlayerSlotData slotData, float timeoutSeconds)
    {
        if (!IsServer) yield break;

        ulong playerNetId = slotData.PlayerNetId;
        pendingInstantiation.Add(playerNetId);

        float start = Time.time;
        NetworkObject containerNetObj = null;

        // 대기 루프: SpawnedObjects에 해당 NetId가 들어올 때까지 기다림
        while (Time.time - start < timeoutSeconds)
        {
            if (NetworkManager.Singleton != null &&
                NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(playerNetId, out containerNetObj) &&
                containerNetObj != null)
            {
                break;
            }
            yield return null;
        }

        pendingInstantiation.Remove(playerNetId);
        if (containerNetObj == null)
        {
            Debug.LogError($"[PlayerCharacterManager] Failed to find container for player {playerNetId} within timeout.");
            yield break;
        }

        // container 준비 완료 -> Instantiate
        GameObject container = containerNetObj.gameObject;
        
        // Container 참조 저장
        containerReferences[playerNetId] = container;
        
        // Container의 렌더러들 비활성화
        foreach (var r in container.GetComponentsInChildren<Renderer>(true))
            r.enabled = false;

        GameObject prefab = characterPrefabs[slotData.SlotNumber];
        if (prefab == null) yield break;

        GameObject instance = Instantiate(prefab, container.transform.position, container.transform.rotation);
        instance.transform.SetParent(null);

        // 캐릭터의 PlayerMovement에 Container 참조 전달
        var characterMovement = instance.GetComponent<PlayerMovement>();
        if (characterMovement != null)
        {
            // Container 참조 설정을 위한 코루틴
            StartCoroutine(SetContainerReferenceAfterSpawn(characterMovement, containerNetObj));
        }

        var instanceNetObj = instance.GetComponent<NetworkObject>();
        if (instanceNetObj != null && IsServer)
        {
            try
            {
                if (!instanceNetObj.IsSpawned)
                    instanceNetObj.SpawnWithOwnership(slotData.ClientId);
                else
                    instanceNetObj.ChangeOwnership(slotData.ClientId);
            }
            catch (System.Exception ex)
            {
                Debug.LogWarning($"Failed to SpawnWithOwnership for client {slotData.ClientId}: {ex}");
            }

            var specMgr = FindFirstObjectByType<PlayerSpectatorManager>();
            if (specMgr != null)
            {
                specMgr.AssignSlotForClient(slotData.ClientId, instanceNetObj.NetworkObjectId);
                Debug.Log($"[PCM] Updated Spectator slot for client {slotData.ClientId} -> netId {instanceNetObj.NetworkObjectId}");
            }

            var rpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { slotData.ClientId } }
            };
            AssignClientFollowTargetClientRpc(instanceNetObj.NetworkObjectId, rpcParams);
        }

        spawnedCharacters[playerNetId] = instance;
        OnCharacterSpawned?.Invoke(instance);

        Debug.Log($"[PCM] Character spawned for client {slotData.ClientId}, container: {container.name}, character: {instance.name}");
    }

    private IEnumerator SetContainerReferenceAfterSpawn(PlayerMovement characterMovement, NetworkObject containerNetObj)
    {
        // 네트워크 스폰이 완료될 때까지 대기
        yield return new WaitForEndOfFrame();
        
        if (characterMovement != null)
        {
            // PlayerMovement에서 Container 참조를 찾도록 요청
            characterMovement.StartCoroutine(FindContainerAfterSpawn(containerNetObj));
        }
    }

    private IEnumerator FindContainerAfterSpawn(NetworkObject containerNetObj)
    {
        yield return new WaitForSeconds(0.1f); // 짧은 대기
        // 여기서 추가 로직이 필요하면 구현
    }

    [ClientRpc]
    private void AssignClientFollowTargetClientRpc(ulong characterNetworkId, ClientRpcParams clientRpcParams = default)
    {
        // 이 ClientRpc은 (대상)클라이언트에서 실행됨 — 해당 클라이언트에서 카메라를 바로 할당
        if (NetworkManager.Singleton == null)
            return;

        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(characterNetworkId, out var charNetObj) && charNetObj != null)
        {
            ApplyCameraFollowOnLocal(charNetObj.transform);
            return;
        }

        // 타이밍 문제 대비: 조금 기다렸다가 시도
        StartCoroutine(DelayedAssign(characterNetworkId, 2f));
    }

    private IEnumerator DelayedAssign(ulong networkId, float timeout)
    {
        float start = Time.time;
        while (Time.time - start < timeout)
        {
            if (NetworkManager.Singleton != null &&
                NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(networkId, out var charNetObj) &&
                charNetObj != null)
            {
                ApplyCameraFollowOnLocal(charNetObj.transform);
                yield break;
            }
            yield return null;
        }
    }

    private void ApplyCameraFollowOnLocal(Transform characterTransform)
    {
        // 로컬에서 실행됨. Camera.main에 CameraFollow 컴포넌트가 있다고 가정.
        if (Camera.main != null)
        {
            var camFollow = Camera.main.GetComponent<CameraFollow>();
            if (camFollow != null)
            {
                camFollow.SetFollowTarget(characterTransform);
                Debug.Log($"[PlayerCharacterManager] Assigned CameraFollow target to {characterTransform.name}");
                return;
            }
        }

        // 혹시 CameraFollow가 다른 위치에 있으면 찾아서 적용
        var localCamFollow = FindFirstObjectByType<CameraFollow>();
        if (localCamFollow != null)
        {
            localCamFollow.SetFollowTarget(characterTransform);
            Debug.Log($"[PlayerCharacterManager] Assigned CameraFollow (via Find) target to {characterTransform.name}");
        }
    }

    // 외부에서 플레이어 추가 시 사용할 메서드
    public void AddPlayer(ulong clientId, ulong playerNetId)
    {
        if (!IsServer) return;

        // 이미 slotDataList에 들어있으면 return
        if (slotDataList.Value.PlayerSlots.Exists(s => s.PlayerNetId == playerNetId))
        {
            Debug.LogWarning($"[PCM] AddPlayer: playerNetId {playerNetId} already in slotDataList");
            return;
        }

        int assignedSlot = GetOrAssignSlotIndex(clientId);
        if (assignedSlot < 0)
        {
            Debug.LogWarning("[PCM] AddPlayer: no available slot");
            return;
        }

        var newSlot = new PlayerSlotData { PlayerNetId = playerNetId, SlotNumber = assignedSlot, ClientId = clientId };
        var currentSlots = new List<PlayerSlotData>(slotDataList.Value.PlayerSlots) { newSlot };
        UpdateSlots(currentSlots);

        Debug.Log($"[PCM] AddPlayer: client {clientId} -> slot {assignedSlot} (playerNetId {playerNetId})");
    }

    // 플레이어 제거 시 사용할 메서드
    public void RemovePlayer(ulong playerNetId)
    {
        if (!IsServer) return;

        var currentSlots = new List<PlayerSlotData>(slotDataList.Value.PlayerSlots);
        var found = currentSlots.FirstOrDefault(s => s.PlayerNetId == playerNetId);
        if (found.PlayerNetId == 0)
        {
            Debug.LogWarning($"[PCM] RemovePlayer: no slot found for playerNetId {playerNetId}");
            return;
        }

        // 먼저 슬롯 리스트에서 제거
        currentSlots.RemoveAll(s => s.PlayerNetId == playerNetId);
        UpdateSlots(currentSlots);

        // spawned 캐릭터 정리
        if (spawnedCharacters.TryGetValue(playerNetId, out var characterGO))
        {
            if (characterGO != null)
            {
                var netObj = characterGO.GetComponent<NetworkObject>();
                if (netObj != null && netObj.IsSpawned)
                {
                    netObj.Despawn(true);
                }
                else Destroy(characterGO);
            }
            spawnedCharacters.Remove(playerNetId);
        }

        // Container 정리
        if (containerReferences.TryGetValue(playerNetId, out var containerGO))
        {
            if (containerGO != null)
            {
                var netObj = containerGO.GetComponent<NetworkObject>();
                if (netObj != null && netObj.IsSpawned)
                {
                    netObj.Despawn(true);
                }
                else Destroy(containerGO);
            }
            containerReferences.Remove(playerNetId);
        }

        // slotAssignments에서 clientId 기준으로 해제
        ReleaseSlotForClient(found.ClientId);

        Debug.Log($"[PCM] RemovePlayer: removed playerNetId {playerNetId}, freed slot {found.SlotNumber} for client {found.ClientId}");
    }

    // 정적 변수 리셋 메서드 (게임 재시작 시 사용)
    public static void ResetSlotAllocation()
    {
        slotAssignments.Clear();
    }

    // PlayerCharacterManager.cs 안에 추가
    public void DestroyContainerWithCharacter(GameObject character)
    {
        ulong playerNetId = 0;
        foreach (var kv in spawnedCharacters)
        {
            if (kv.Value == character)
            {
                playerNetId = kv.Key;
                break;
            }
        }

        if (playerNetId != 0)
        {
            // 캐릭터 제거
            if (spawnedCharacters.TryGetValue(playerNetId, out var characterGO))
            {
                if (characterGO != null)
                {
                    var netObj = characterGO.GetComponent<NetworkObject>();
                    if (netObj != null && netObj.IsSpawned)
                        netObj.Despawn(true);
                    else
                        Destroy(characterGO);
                }
                spawnedCharacters.Remove(playerNetId);
            }

            // 컨테이너 제거
            if (containerReferences.TryGetValue(playerNetId, out var containerGO))
            {
                if (containerGO != null)
                {
                    var netObj = containerGO.GetComponent<NetworkObject>();
                    if (netObj != null && netObj.IsSpawned)
                        netObj.Despawn(true);
                    else
                        Destroy(containerGO);
                }
                containerReferences.Remove(playerNetId);
            }
        }
    }

    public GameObject GetCharacterByClientId(ulong clientId)
    {
        // ClientId로 해당하는 캐릭터 GameObject 찾기
        foreach (var kvp in spawnedCharacters)
        {
            var characterGO = kvp.Value;
            if (characterGO != null)
            {
                var netObj = characterGO.GetComponent<NetworkObject>();
                if (netObj != null && netObj.OwnerClientId == clientId)
                {
                    return characterGO;
                }
            }
        }
        return null;
    }

    public GameObject GetContainerByClientId(ulong clientId)
    {
        // ClientId로 해당하는 컨테이너 GameObject 찾기
        foreach (var kvp in containerReferences)
        {
            var containerGO = kvp.Value;
            if (containerGO != null)
            {
                var netObj = containerGO.GetComponent<NetworkObject>();
                if (netObj != null && netObj.OwnerClientId == clientId)
                {
                    return containerGO;
                }
            }
        }
        return null;
    }
}