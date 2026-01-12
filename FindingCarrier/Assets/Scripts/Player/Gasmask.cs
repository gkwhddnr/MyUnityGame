using UnityEngine;
using Unity.Netcode;

public class Gasmask : NetworkBehaviour
{
    [Header("Input")]
    public KeyCode equipKey = KeyCode.X;

    // 로컬 소유자에서 X 누르면 서버에 요청
    void Update()
    {
        if (!IsOwner) return;
        if (Input.GetKeyDown(equipKey))
        {
            RequestEquipGasmaskServerRpc();
        }
    }

    // 서버로 요청: 이 RPC는 호출자(플레이어) 소유의 object에서만 호출되어야 함.
    [ServerRpc(RequireOwnership = false)]
    private void RequestEquipGasmaskServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;

        ulong requester = rpcParams.Receive.SenderClientId;

        if (DayNightManager.Instance != null && DayNightManager.Instance.isNight.Value)
        {
            var clientParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { requester } } };
            GasmaskEquipResultClientRpc(false, "밤에는 방독면을 착용(사용)할 수 없습니다.", clientParams);
            return;
        }

        // 대상 캐릭터의 InfectionStatus를 찾아서 서버에서 설정
        InfectionStatus inf = GetComponent<InfectionStatus>() ?? GetComponentInChildren<InfectionStatus>() ?? GetComponentInParent<InfectionStatus>();
        if (inf == null)
        {
            var pcm = FindFirstObjectByType<PlayerCharacterManager>();
            if (pcm != null)
            {
                var charGO = pcm.GetCharacterByClientId(requester);
                if (charGO != null)
                {
                    inf = charGO.GetComponent<InfectionStatus>() ?? charGO.GetComponentInChildren<InfectionStatus>() ?? charGO.GetComponentInParent<InfectionStatus>();
                }
            }
        }

        // 4) 폴백: SpawnedObjects에서 OwnerClientId로 찾아보기 (보완)
        if (inf == null && NetworkManager.Singleton != null)
        {
            foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
            {
                var no = kv.Value;
                if (no == null) continue;
                if (no.OwnerClientId != requester) continue;
                inf = no.GetComponent<InfectionStatus>() ?? no.GetComponentInChildren<InfectionStatus>() ?? no.GetComponentInParent<InfectionStatus>();
                if (inf != null) break;
            }
        }

        if (inf == null)
        {
            // 만약 컨테이너/구조상 InfectionStatus가 부모/자식에 있다면 탐색
            inf = GetComponentInChildren<InfectionStatus>() ?? GetComponentInParent<InfectionStatus>();
            if (inf == null)
            {
                var clientParams2 = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { requester } } };
                GasmaskEquipResultClientRpc(false, "방독면 장착 실패: 플레이어 상태를 찾을 수 없습니다.", clientParams2);
                return;
            }
        }

        // 이미 사용했다면 무시
        if (inf.IsGasmaskUsed.Value)
        {
            // optional: 개인에게 실패 알림
            var clientParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { requester } } };
            GasmaskEquipResultClientRpc(false, "방독면은 이미 사용했습니다.", clientParams);
            return;
        }

        // 서버에서 방독면 장착 처리 (IsGasmaskEquipped true, IsGasmaskUsed true)
        inf.SetGasmaskServer(true);

        // 확인 메시지 (개인)
        var okParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { requester } } };
        GasmaskEquipResultClientRpc(true, "방독면을 착용했습니다. (1회 사용)", okParams);
    }

    [ClientRpc]
    private void GasmaskEquipResultClientRpc(bool ok, string message, ClientRpcParams rpcParams = default)
    {
        if (PersonalNotificationManager.Instance != null)
        {
            PersonalNotificationManager.Instance.ShowPersonalMessage(message);
            return;
        }

        // 로컬 개인 UI 알림
        var pm = FindFirstObjectByType<PersonalNotificationManager>();
        pm?.ShowPersonalMessage(message);
    }
}
