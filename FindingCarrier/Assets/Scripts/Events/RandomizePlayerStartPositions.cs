using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public class RandomizePlayerStartPositions : NetworkBehaviour
{
    [Tooltip("Plane 오브젝트의 Layer를 지정하세요.")]
    public LayerMask planeLayer;

    [Tooltip("검색할 MeshCollider 배열 (여러 개 지정 가능)")]
    public MeshCollider[] searchVolumes;

    [Tooltip("플레인 위 y 위치 오프셋 (플레이어가 땅에 박히지 않게 위로 띄움)")]
    public float heightOffset = 0.5f;

    [Tooltip("플레이어 위치를 뽑을 때 시도 횟수 (충돌 회피 등)")]
    public int maxAttemptsPerPlayer = 10;

    private void OnEnable()
    {
        LightManager.OnGameStarted += OnGameStarted;
    }

    private void OnDisable()
    {
        LightManager.OnGameStarted -= OnGameStarted;
    }

    private void OnGameStarted()
    {
        // 이벤트가 서버에서 발생하므로 안전하게 서버 체크
        if (!IsServer) return;

        RandomizeAllPlayers();
    }

    private void RandomizeAllPlayers()
    {
        var clients = NetworkManager.Singleton.ConnectedClientsList;
        var playersToTeleport = new List<(NetworkObject characterNetObj, NetworkObject containerNetObj, GameObject characterGO)>();

        // PlayerCharacterManager에서 실제 캐릭터 가져오기
        var pcm = FindFirstObjectByType<PlayerCharacterManager>();
        if (pcm == null)
        {
            Debug.LogWarning("[RandomizePlayerStartPositions] PlayerCharacterManager not found!");
            return;
        }

        // 1) 모든 클라이언트에 대해 characterNetObj + containerNetObj 수집 (서버 전용)
        foreach (var client in clients)
        {
            ulong clientId = client.ClientId;
            // 먼저 pcm의 helper 사용
            GameObject characterGO = pcm.GetCharacterByClientId(clientId);
            GameObject containerGO = pcm.GetContainerByClientId(clientId);

            // 폴백: SpawnedObjects에서 OwnerClientId 기준으로 찾기 (character)
            NetworkObject charNetObj = null;
            NetworkObject containerNetObj = null;

            if (characterGO != null)
                charNetObj = characterGO.GetComponent<NetworkObject>();

            if (containerGO != null)
                containerNetObj = containerGO.GetComponent<NetworkObject>();

            if (charNetObj == null || containerNetObj == null)
            {
                // 폴백 검색: SpawnedObjects 중 OwnerClientId == clientId 인 것들을 검색
                foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
                {
                    var netObj = kv.Value;
                    if (netObj == null) continue;
                    if (netObj.OwnerClientId != clientId) continue;

                    // container 찾기 (이름에 Container 포함되거나, PlayerMovement가 없고 컨테이너 스크립트가 붙어있을 가능성)
                    if (containerNetObj == null && netObj.gameObject.name.Contains("Container"))
                    {
                        containerNetObj = netObj;
                        containerGO = netObj.gameObject;
                    }

                    // character 찾기: PlayerMovement 있고 IsCharacterInstance이면 캐릭터
                    if (charNetObj == null)
                    {
                        var pm = netObj.GetComponent<PlayerMovement>();
                        if (pm != null && pm.IsCharacterInstance())
                        {
                            charNetObj = netObj;
                            characterGO = netObj.gameObject;
                        }
                    }

                    if (charNetObj != null && containerNetObj != null) break;
                }
            }

            // 마지막 폴백: client.PlayerObject 가 실제 캐릭터일 수 있음
            if (charNetObj == null && client.PlayerObject != null)
            {
                var pmCheck = client.PlayerObject.GetComponent<PlayerMovement>();
                if (pmCheck != null && pmCheck.IsCharacterInstance())
                {
                    charNetObj = client.PlayerObject;
                    characterGO = client.PlayerObject.gameObject;
                }
            }

            if (charNetObj == null)
            {
                Debug.LogWarning($"[RandomizePlayerStartPositions] Character not found for client {clientId}");
                continue;
            }

            playersToTeleport.Add((charNetObj, containerNetObj, characterGO));
            Debug.Log($"[RandomizePlayerStartPositions] Queued character for client {clientId}: {charNetObj.gameObject.name}");
        }

        if (playersToTeleport.Count == 0)
        {
            Debug.LogWarning("[RandomizePlayerStartPositions] No characters found to teleport!");
            return;
        }

        var planeRenderers = FindPlaneRenderers();
        if (planeRenderers == null || planeRenderers.Count == 0)
        {
            Debug.LogWarning("[RandomizePlayerStartPositions] No plane renderers found!");
            return;
        }

        // 목표 위치 목록: 이미 확정한 위치들과 비교하여 충돌 방지
        var usedPositions = new List<Vector3>();

        foreach (var entry in playersToTeleport)
        {
            Vector3 finalPos = Vector3.zero;
            bool placed = false;

            for (int attempt = 0; attempt < maxAttemptsPerPlayer && !placed; attempt++)
            {
                var renderer = planeRenderers[Random.Range(0, planeRenderers.Count)];
                if (renderer == null) continue;

                Bounds b = renderer.bounds;
                Vector3 candidate = new Vector3(
                    Random.Range(b.min.x, b.max.x),
                    b.max.y + heightOffset,
                    Random.Range(b.min.z, b.max.z)
                );

                bool conflict = false;
                foreach (var used in usedPositions)
                {
                    if (Vector3.Distance(candidate, used) < 1.0f)
                    {
                        conflict = true;
                        break;
                    }
                }

                if (!conflict)
                {
                    finalPos = candidate;
                    placed = true;
                    usedPositions.Add(finalPos);
                }
            }

            if (!placed)
            {
                var r = planeRenderers[0];
                var c = r.bounds.center;
                finalPos = new Vector3(c.x, r.bounds.max.y + heightOffset, c.z);
                usedPositions.Add(finalPos);
            }

            // 서버에서 직접 위치 적용 (NetworkObject 존재 시 SetPositionAndRotation 사용 권장)
            if (entry.characterNetObj != null)
            {
                entry.characterNetObj.transform.SetPositionAndRotation(finalPos, Quaternion.identity);

                // Rigidbody가 있으면 velocity 정리
                var rb = entry.characterNetObj.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.linearVelocity = Vector3.zero;
                    rb.angularVelocity = Vector3.zero;
                }

                // 클라이언트 동기화 RPC
                TeleportPlayerClientRpc(entry.characterNetObj.NetworkObjectId, finalPos);
            }

            if (entry.containerNetObj != null)
            {
                entry.containerNetObj.transform.SetPositionAndRotation(finalPos, Quaternion.identity);

                var rb2 = entry.containerNetObj.GetComponent<Rigidbody>();
                if (rb2 != null)
                {
                    rb2.linearVelocity = Vector3.zero;
                    rb2.angularVelocity = Vector3.zero;
                }

                TeleportContainerClientRpc(entry.containerNetObj.NetworkObjectId, finalPos);
            }

            // 이동 잠금: 컨테이너의 PlayerMovement가 있다면 SetCanMoveTemporaryServerRpc 호출
            if (entry.containerNetObj != null)
            {
                var pm = entry.containerNetObj.GetComponent<PlayerMovement>();
                if (pm != null)
                {
                    // 잠깐 이동 잠금 (duration 0이면 즉시 해제이므로 원래 의도대로 duration을 주려면 값 수정)
                    pm.SetCanMoveTemporaryServerRpc(false, 0f);
                }
            }

            Debug.Log($"[RandomizePlayerStartPositions] Teleported {entry.characterNetObj.gameObject.name} to {finalPos}");
        }
    }

    private List<Renderer> FindPlaneRenderers()
    {
        var list = new List<Renderer>();
        if (searchVolumes == null) return list;

        foreach (var volume in searchVolumes)
        {
            if (volume == null) continue;
            var hits = Physics.OverlapBox(volume.bounds.center, volume.bounds.extents, volume.transform.rotation, planeLayer);
            foreach (var col in hits)
            {
                if (col == null) continue;
                var r = col.GetComponent<Renderer>();
                if (r != null && !list.Contains(r)) list.Add(r);
            }
        }
        return list;
    }

    [ClientRpc]
    private void TeleportPlayerClientRpc(ulong networkId, Vector3 position)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(networkId, out var netObj))
        {
            netObj.transform.position = position;
        }
    }

    [ClientRpc]
    private void TeleportContainerClientRpc(ulong containerNetworkId, Vector3 position)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(containerNetworkId, out var containerNetObj))
        {
            containerNetObj.transform.position = position;
        }
    }

    [ClientRpc]
    private void TeleportChildCharacterClientRpc(ulong containerNetworkId, Vector3 position)
    {
        if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(containerNetworkId, out var containerNetObj))
        {
            // 컨테이너를 해당 위치로 이동
            containerNetObj.transform.position = position;

            // 자식 캐릭터들도 같은 위치로 이동 (localPosition을 0으로 유지)
            foreach (Transform child in containerNetObj.transform)
            {
                child.localPosition = Vector3.zero;
            }
        }
    }
}