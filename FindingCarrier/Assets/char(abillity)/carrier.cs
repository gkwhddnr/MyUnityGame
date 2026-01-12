using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Unity.Netcode;

public class carrier : NetworkBehaviour
{
    [Header("Infect")]
    public float infectRange = 2f;
    public KeyCode infectKey = KeyCode.Z;
    [Tooltip("레이어 이름(인스펙터에 'Carrier' 같은 레이어 이름을 넣으세요).")]
    public string carrierLayerName = "Carrier";
    [Tooltip("감염 판정에 사용할 레이어 마스크 (플레이어 콜라이더만 포함하도록 설정)")]
    public LayerMask infectLayerMask = ~0;

    [Header("Night")]
    public bool serverCanInfect = true;
    public float nightSpeedMultiplier = 1.5f;

    public NetworkVariable<bool> IsCarrier = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private int originalLayer = -1;

    void Update()
    {
        if (!IsOwner) return;
        if (!IsCarrier.Value) return;

        if (DayNightManager.Instance != null && !DayNightManager.Instance.isNight.Value) return;
        

        if (Input.GetKeyDown(infectKey))
        {
            TryInfectLocal();
        }
    }

    private void TryInfectLocal()
    {
        NetworkObject localChar = FindCharacterNetworkObjectForClient(OwnerClientId);
        Vector3 center = (localChar != null) ? localChar.transform.position : transform.position;

        // LayerMask를 사용해 플레이어 콜라이더만 검사
        Collider[] hits = Physics.OverlapSphere(center, infectRange, infectLayerMask);
        foreach (var c in hits)
        {
            if (c == null) continue;
            var netObj = c.GetComponentInParent<NetworkObject>() ?? c.GetComponent<NetworkObject>();
            if (netObj == null) continue;

            ulong targetOwner = netObj.OwnerClientId;
            if (targetOwner == OwnerClientId) continue;

            // 서버에게 요청 (targetOwner를 전달)
            RequestInfectServerRpc(targetOwner);
            return;
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestInfectServerRpc(ulong targetOwnerClientId, ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;

        if (DayNightManager.Instance != null && !DayNightManager.Instance.isNight.Value)
        {
            var rpDay = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { rpcParams.Receive.SenderClientId } } };
            InfectFeedbackClientRpc(false, "낮에는 능력을 사용할 수 없습니다.", rpDay);
            return;
        }

        ulong requester = rpcParams.Receive.SenderClientId;
        // 보안 검사: 요청자는 이 component의 owner여야 함
        var myNetObj = this.GetComponent<NetworkObject>();
        if (myNetObj == null || myNetObj.OwnerClientId != requester) return;


        if (!serverCanInfect)
        {
            // 이미 사용
            var rp = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { requester } } };
            InfectFeedbackClientRpc(false, "이미 능력을 사용했습니다.", rp);
            return;
        }

        var targetChar = FindCharacterNetworkObjectForClient(targetOwnerClientId);
        if (targetChar == null)
        {
            var rp = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { requester } } };
            InfectFeedbackClientRpc(false, "대상을 찾을 수 없습니다.", rp);
            return;
        }

        var inf = targetChar.GetComponent<InfectionStatus>() ?? targetChar.GetComponentInChildren<InfectionStatus>() ?? targetChar.GetComponentInParent<InfectionStatus>();
        if (inf == null)
        {
            var rp = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { requester } } };
            InfectFeedbackClientRpc(false, "대상에 감염 컴포넌트가 없음", rp);
            return;
        }

        // 보호/면역/방독면 여부는 감염 '불가' 판단이 아니라 다음날 판정으로 처리 -> 따라서 여기서는 감염을 여전히 설정
        inf.SetInfectedServer(true);
        serverCanInfect = false;

        // 보균자에게만 성공 알림 전송 (대상에는 즉시 통지하지 않음)
        var okParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { requester } } };
        InfectFeedbackClientRpc(true, $"감염 성공: {GetPlayerNameForClient(targetOwnerClientId)} 플레이어를 감염시켰습니다.", okParams);
    }

    [ClientRpc]
    private void InfectFeedbackClientRpc(bool success, string message, ClientRpcParams rpcParams = default)
    {
        PersonalNotificationManager.Instance?.ShowPersonalMessage(message);
    }

    // 밤/낮 이벤트 처리(서버)
    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer)
        {
            serverCanInfect = true;
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
            DayNightManager.Instance.onNightStart.RemoveListener(OnNightStartServer);
            DayNightManager.Instance.onDayStart.RemoveListener(OnDayStartServer);
        }
    }
    private void OnNightStartServer()
    {
        if (!IsServer) return;
        serverCanInfect = true;

        var charNetObj = FindCharacterNetworkObjectForClient(OwnerClientId);
        if (charNetObj == null) return;
        var pm = charNetObj.GetComponent<PlayerMovement>();
        if (pm != null)
        {
            pm.ServerSetCanMove(true);
            pm.SetSpeedMultiplierServerRpc(nightSpeedMultiplier);
        }
    }
    private void OnDayStartServer()
    {
        if (!IsServer) return;
        var charNetObj = FindCharacterNetworkObjectForClient(OwnerClientId);
        if (charNetObj == null) return;
        var pm = charNetObj.GetComponent<PlayerMovement>();
        if (pm != null)
        {
            pm.SetSpeedMultiplierServerRpc(1f);
        }
    }

    // helper: 기존과 동일한 방식으로 owner의 캐릭터 NetworkObject 찾기
    private NetworkObject FindCharacterNetworkObjectForClient(ulong ownerClientId)
    {
        // 우선 PlayerCharacterManager의 spawnedCharacters에 매핑이 있으면 그 값을 우선 사용
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
                        var netObj = go.GetComponentInParent<NetworkObject>() ?? go.GetComponent<NetworkObject>();
                        if (netObj != null && netObj.OwnerClientId == ownerClientId) return netObj;
                    }
                }
            }
            catch { /* 안전하게 폴백 처리 */ }
        }

        // 폴백: NetworkManager의 SpawnedObjects 검색
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

        // 마지막 폴백: 씬의 PlayerMovement 검색 (inactive 포함)
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

    private string GetPlayerNameForClient(ulong clientId)
    {
        if (NetworkManager.Singleton == null) return $"Player{clientId}";
        if (NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client))
        {
            var obj = client.PlayerObject;
            if (obj != null)
            {
                var pm = obj.GetComponent<PlayerMovement>();
                if (pm != null) return pm.playerName.Value.ToString();
            }
        }
        return $"Player{clientId}";
    }

    public void AssignAsCarrierServer()
    {
        if (!IsServer) return;




        IsCarrier.Value = true;
        serverCanInfect = true;

        var charNetObj = FindCharacterNetworkObjectForClient(OwnerClientId);
        if (charNetObj != null)
        {
            // store original layer (approximation)
            originalLayer = charNetObj.gameObject.layer;
            int layerIndex = LayerMask.NameToLayer(carrierLayerName);
            if (layerIndex >= 0)
            {
                SetLayerRecursive(charNetObj.gameObject, layerIndex);

                // Container(있다면)도 변경 (PlayerCharacterManager 참조 사용)
                var pcm = FindFirstObjectByType<PlayerCharacterManager>();
                if (pcm != null)
                {
                    var container = pcm.GetContainerByClientId(OwnerClientId);
                    if (container != null)
                        SetLayerRecursive(container, layerIndex);
                }

                Debug.Log($"carrier: Assigned carrier layer '{carrierLayerName}' ({layerIndex}) to {charNetObj.name}");
            }

            // 즉시 이동 허용: Carrier로 지정되면 낮/밤 상관없이 이동 가능하도록 강제 세팅
            var pm = charNetObj.GetComponent<PlayerMovement>()
                     ?? charNetObj.GetComponentInChildren<PlayerMovement>()
                     ?? charNetObj.GetComponentInParent<PlayerMovement>();

            if (pm.AssignedRole.Value.ToString() != "Carrier") return;
            if (pm != null)
            {
                pm.CanMoveVar.Value = true;

                // 밤이면 속도 multiplier 적용, 낮이면 기본(1) 적용
                if (DayNightManager.Instance != null && DayNightManager.Instance.isNight.Value)
                    pm.SetSpeedMultiplierServerRpc(nightSpeedMultiplier);
                else
                    pm.SetSpeedMultiplierServerRpc(1f);

                Debug.Log($"carrier: Immediately enabled movement for carrier {charNetObj.name} (Owner {OwnerClientId}).");
            }
        }
    }

    public void RevokeCarrierServer()
    {
        if (!IsServer) return;

        IsCarrier.Value = false;
        serverCanInfect = false;

        var charNetObj = FindCharacterNetworkObjectForClient(OwnerClientId);
        if (charNetObj != null && originalLayer >= 0)
        {
            SetLayerRecursive(charNetObj.gameObject, originalLayer);

            var pcm = FindFirstObjectByType<PlayerCharacterManager>();
            if (pcm != null)
            {
                var container = pcm.GetContainerByClientId(OwnerClientId);
                if (container != null) SetLayerRecursive(container, originalLayer);
            }

            // Carrier 해제 -> 지금이 밤이면 이동 금지, 낮이면 이동 허용(기본 규칙)
            bool nowNight = (DayNightManager.Instance != null && DayNightManager.Instance.isNight.Value);
            var pm = charNetObj.GetComponent<PlayerMovement>() ?? charNetObj.GetComponentInChildren<PlayerMovement>() ?? charNetObj.GetComponentInParent<PlayerMovement>();
            if (pm != null)
            {
                pm.ServerSetCanMove(!nowNight);
                pm.SetSpeedMultiplierServerRpc(1f);
            }
        }
        else
        {
            Debug.LogWarning($"carrier.RevokeCarrierServer: cannot restore layer (char not found or originalLayer unset) for Owner {OwnerClientId}");
        }
    }

    private void SetLayerRecursive(GameObject go, int layer)
    {
        if (go == null) return;
        go.layer = layer;
        foreach (Transform t in go.transform)
        {
            SetLayerRecursive(t.gameObject, layer);
        }
    }

    public void ResetForNightServer()
    {
        if (!IsServer) return;
        serverCanInfect = true;
    }

}
