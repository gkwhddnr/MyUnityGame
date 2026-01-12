using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;
using System.Linq;


public class GameStartManager : NetworkBehaviour
{
    public static GameStartManager Instance;

    [Header("Carrier settings")]
    public float carrierNightSpeedMultiplier = 1.5f;

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);
    }

    private void Start()
    {
        if (IsServer)
        {
            // DayNightManager가 있다면 아침 이벤트 구독 (서버에서만)
            if (DayNightManager.Instance != null)
            {
                DayNightManager.Instance.onNightStart.AddListener(OnNightStartServer);
                DayNightManager.Instance.onDayStart.AddListener(OnDayStartServer);
            }
        }
    }

    public override void OnDestroy()
    {
        if (IsServer && DayNightManager.Instance != null)
        {
            DayNightManager.Instance.onNightStart.AddListener(OnNightStartServer);
            DayNightManager.Instance.onDayStart.RemoveListener(OnDayStartServer);
        }
    }

    [ClientRpc]
    private void SetupCameraFollowClientRpc(ClientRpcParams rpcParams = default)
    {
        // 안전하게 코루틴으로 재시도
        StartCoroutine(SetupCameraRoutine());
    }

    private IEnumerator SetupCameraRoutine()
    {
        float start = Time.time;
        float timeout = 5f;

        while (Time.time - start < timeout)
        {
            if (Camera.main != null)
            {
                var camFollow = Camera.main.GetComponent<CameraFollow>();
                if (camFollow != null)
                {
                    camFollow.ForceInitializeAndBind();
                    yield break;
                }
            }
            yield return null;
        }
    }


    private void OnNightStartServer()
    {
        if (!IsServer) return;

        // 1) 먼저 전체 플레이어 이동 차단
        if (NetworkManager.Singleton != null)
        {
            foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
            {
                var no = kv.Value;
                if (no == null) continue;

                var pm = no.GetComponent<PlayerMovement>();
                if (pm != null && pm.IsCharacterInstance())
                {
                    pm.CanMoveVar.Value = false;
                    pm.SetSpeedMultiplierServerRpc(1f);
                }
            }
        }

        // 2) 보균자들만 찾아서 이동 허용 + 속도 증가
        if (NetworkManager.Singleton != null)
        {
            foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
            {
                var no = kv.Value;
                if (no == null) continue;

                // carrier 컴포넌트가 object 자체나 자식/부모에 붙어있을 수 있으므로 폭넓게 찾는다
                var carrierComp = no.GetComponent<carrier>() ?? no.GetComponentInChildren<carrier>() ?? no.GetComponentInParent<carrier>();
                if (carrierComp != null && carrierComp.IsCarrier.Value)
                {
                    // 실제 캐릭터 NetworkObject 찾아 PlayerMovement 조작
                    var charNetObj = FindCharacterNetworkObjectForClient(carrierComp.OwnerClientId);
                    if (charNetObj == null) continue;

                    var pm = charNetObj.GetComponent<PlayerMovement>();
                    if (pm != null)
                    {
                        pm.CanMoveVar.Value = true;
                        pm.SetSpeedMultiplierServerRpc(carrierComp.nightSpeedMultiplier);
                    }

                    // 능력 리셋
                    carrierComp.ResetForNightServer();
                }
            }
        }
    }

    // UI 버튼에서 호출 (클라이언트/호스트가 누르면 서버 RPC로 요청)
    public void RequestStartGame()
    {
        // 클라이언트에서 호출 -> 서버가 시작하도록 ServerRpc 요청
        StartGameServerRpc();
    }

    [ServerRpc(RequireOwnership = false)]
    private void StartGameServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;

        // 선택 규칙: 지금은 랜덤 1명 선택. 필요하면 룰 변경.
        var clients = NetworkManager.Singleton.ConnectedClientsList;
        if (clients == null || clients.Count == 0) return;

        int idx = Random.Range(0, clients.Count);
        ulong chosenClientId = clients[idx].ClientId;

        // 1) 먼저 모든 기존 IsCarrier를 초기화
        ResetAllCarriersServer();

        // 2) 선택된 플레이어의 캐릭터 NetworkObject 찾기
        var chosenChar = FindCharacterNetworkObjectForClient(chosenClientId);
        if (chosenChar == null) return;

        // 3) carrier 컴포넌트 찾고 서버에서 활성화(네트워크 변수 설정)
        var carrierComp = chosenChar.GetComponent<carrier>() ?? chosenChar.GetComponentInChildren<carrier>() ?? chosenChar.GetComponentInParent<carrier>();
        if (carrierComp == null)
        {
            // 폴백: PlayerCharacterManager가 가진 container 쪽에 있을 수 있음
            var pcm = FindFirstObjectByType<PlayerCharacterManager>();
            if (pcm != null)
            {
                var container = pcm.GetContainerByClientId(chosenClientId);
                if (container != null)
                {
                    carrierComp = container.GetComponent<carrier>() ?? container.GetComponentInChildren<carrier>() ?? container.GetComponentInParent<carrier>();
                }
            }
        }

        carrierComp.AssignAsCarrierServer();

        var chosenCharNet = FindCharacterNetworkObjectForClient(chosenClientId);
        if (chosenCharNet != null)
        {
            var pm = chosenCharNet.GetComponent<PlayerMovement>() ?? chosenCharNet.GetComponentInChildren<PlayerMovement>() ?? chosenCharNet.GetComponentInParent<PlayerMovement>();
            if (pm != null)
            {
                pm.AssignedRole.Value = new Unity.Collections.FixedString128Bytes("Carrier");
                pm.ServerSetCanMove(true);
                pm.SetSpeedMultiplierServerRpc(DayNightManager.Instance != null && DayNightManager.Instance.isNight.Value ? carrierNightSpeedMultiplier : 1f);
                Debug.Log($"GameStartManager: Forced CanMoveVar=true for carrier client {chosenClientId}");
            }
            else
            {
                // 만약 아직 PlayerMovement가 준비 안 되었다면 짧게 재시도하는 코루틴을 돌릴 수도 있음
                StartCoroutine(DelayedForceEnableMovement(chosenCharNet.NetworkObjectId, 1.0f));
            }
        }

        SetupCameraFollowClientRpc();
    }

    private IEnumerator DelayedForceEnableMovement(ulong charNetId, float timeout)
    {
        float start = Time.time;
        while (Time.time - start < timeout)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(charNetId, out var netObj) && netObj != null)
            {
                var pm = netObj.GetComponent<PlayerMovement>() ?? netObj.GetComponentInChildren<PlayerMovement>() ?? netObj.GetComponentInParent<PlayerMovement>();
                if (pm != null)
                {
                    pm.CanMoveVar.Value = true;
                    pm.SetSpeedMultiplierServerRpc(DayNightManager.Instance != null && DayNightManager.Instance.isNight.Value ? carrierNightSpeedMultiplier : 1f);
                    Debug.Log($"DelayedForceEnableMovement: applied CanMoveVar for {charNetId}");
                    yield break;
                }
            }
            yield return null;
        }
    }


    // 서버에서 모든 플레이어의 carrier.IsCarrier 초기화
    private void ResetAllCarriersServer()
    {
        if (!IsServer) return;
        if (NetworkManager.Singleton == null) return;

        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var no = kv.Value;
            if (no == null) continue;
            var c = no.GetComponent<carrier>() ?? no.GetComponentInChildren<carrier>() ?? no.GetComponentInParent<carrier>();
            if (c != null)
            {
                c.RevokeCarrierServer();
            }
        }
    }

    private NetworkObject FindCharacterNetworkObjectForClient(ulong ownerClientId)
    {
        var pcm = FindFirstObjectByType<PlayerCharacterManager>();
        if (pcm != null)
        {
            try
            {
                if (pcm.spawnedCharacters != null)
                {
                    foreach (var kv in pcm.spawnedCharacters)
                    {
                        var go = kv.Value;
                        if (go == null) continue;
                        var no = go.GetComponentInParent<NetworkObject>() ?? go.GetComponent<NetworkObject>();
                        if (no != null && no.OwnerClientId == ownerClientId) return no;
                    }
                }
            }
            catch { }
        }

        // 2) SpawnedObjects 검색
        if (NetworkManager.Singleton != null)
        {
            foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
            {
                var no = kv.Value;
                if (no == null) continue;
                if (no.OwnerClientId != ownerClientId) continue;
                if (no.GetComponent<PlayerHealth>() != null) return no;
                var pm = no.GetComponent<PlayerMovement>();
                if (pm != null && pm.IsCharacterInstance()) return no;
            }
        }

        // 3) 씬 검색 폴백
#if UNITY_2023_2_OR_NEWER
        var all = UnityEngine.Object.FindObjectsByType<PlayerMovement>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        var all = Resources.FindObjectsOfTypeAll<PlayerMovement>();
#endif
        foreach (var p in all)
        {
            if (p == null) continue;
            if (p.OwnerClientId == ownerClientId && p.IsCharacterInstance())
            {
                var no = p.GetComponentInParent<NetworkObject>() ?? p.GetComponent<NetworkObject>();
                if (no != null) return no;
            }
        }

        return null;
    }

    private void OnDayStartServer()
    {
        if (!IsServer) return;

        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var no = kv.Value;
            if (no == null) continue;
            var pm = no.GetComponent<PlayerMovement>();
            if (pm != null && pm.IsCharacterInstance())
            {
                pm.CanMoveVar.Value = true;
                pm.SetSpeedMultiplierServerRpc(1f);
            }
        }
    }
}
