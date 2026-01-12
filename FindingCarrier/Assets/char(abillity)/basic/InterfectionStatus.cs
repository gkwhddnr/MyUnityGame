using UnityEngine;
using Unity.Netcode;
using System.Collections;

public class InfectionStatus : NetworkBehaviour
{
    // 감염 여부 (서버가 설정)
    public NetworkVariable<bool> IsInfected = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 밤에 감염 표시 (밤 동안 감염된 경우에만 서버가 설정하고, 아침 판정 후 클리어)
    public NetworkVariable<bool> InfectedThisNight = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 보균자 여부(초기 역할 정할 때 서버가 설정할 수 있음)
    public NetworkVariable<bool> IsCarrier = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 보호(간호사 등) - 서버에서 설정
    public NetworkVariable<bool> IsUnderProtection = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 약사에 의한 면역 - 서버에서 설정
    public NetworkVariable<bool> IsImmuneFromPharmacist = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 방독면 착용 상태 (착용하면 true, 아침 판정에서 소모/해제)
    public NetworkVariable<bool> IsGasmaskEquipped = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 이미 방독면을 '사용'했는지 (한 번만 사용 가능)
    public NetworkVariable<bool> IsGasmaskUsed = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 밤 동안 누가 감염 요청을 받았는지(밤중 pending)
    public NetworkVariable<bool> PendingInfection = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 이 값은 밤 시작시에 서버에서 설정되고, 아침의 판정에서 사용합니다.
    public NetworkVariable<bool> ProtectedThisNight = new NetworkVariable<bool>(
        false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    [Header("Zombie")]
    public GameObject zombieNetworkPrefab;

    public float morningConversionDelay = 5f;

    private void Start()
    {
        IsInfected.OnValueChanged += OnInfectedChanged;
    }

    public override void OnDestroy()
    {
        IsInfected.OnValueChanged -= OnInfectedChanged;
        if (IsServer && DayNightManager.Instance != null)
        {
            DayNightManager.Instance.onDayStart.RemoveListener(OnDayStartServer);
        }
        base.OnDestroy();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        if (IsServer)
        {
            if (DayNightManager.Instance != null)
            {
                DayNightManager.Instance.onDayStart.AddListener(OnDayStartServer);
            }
        }
    }

    private void OnInfectedChanged(bool oldVal, bool newVal)
    {
        // 로컬 이펙트/데코레이션 처리(원하면 여기에 추가)
        if (newVal)
            Debug.Log($"{gameObject.name} 네트워크: 감염 상태가 되었습니다.");
        else
            Debug.Log($"{gameObject.name} 네트워크: 감염 상태가 해제되었습니다.");
    }

    // 서버 전용 helper
    public void SetInfectedServer(bool infected)
    {
        if (!IsServer) return;

        // IsInfected는 단순 현재 감염 표시
        IsInfected.Value = infected;

        if (infected)
        {
            InfectedThisNight.Value = (DayNightManager.Instance != null) ? DayNightManager.Instance.isNight.Value : true;
        }
        else
        {
            InfectedThisNight.Value = false;
        }
    }

    public void SetPendingInfectionServer(bool pending)
    {
        if (!IsServer) return;

        if (pending)
        {
            // 이미 pending이면 그대로 둠
            if (PendingInfection.Value) return;

            PendingInfection.Value = true;

            // 이 시점의 보호 상태를 스냅샷하여 아침 판정 때 사용
            bool protectedNow = IsUnderProtection.Value || IsImmuneFromPharmacist.Value || IsGasmaskEquipped.Value;
            ProtectedThisNight.Value = protectedNow;

            // 레거시 표시: 밤에 감염표시용 (디버깅/호환성)
            InfectedThisNight.Value = true;

            Debug.Log($"[InfectionStatus] {gameObject.name} PendingInfection set=true, ProtectedThisNight={protectedNow}");
        }
        else
        {
            // clear
            PendingInfection.Value = false;
            ProtectedThisNight.Value = false;
            InfectedThisNight.Value = false;
        }
    }

    public void SetGasmaskServer(bool equipped)
    {
        if (!IsServer) return;

        if (DayNightManager.Instance != null && DayNightManager.Instance.isNight.Value)
        {
            // 개인에게 피드백 전송
            ulong owner = GetOwnerClientId();
            var rp = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { owner } } };
            NotifyClientRpc("밤에는 방독면을 착용(사용)할 수 없습니다.", rp);
            return;
        }

        IsGasmaskEquipped.Value = equipped;
        if (equipped) IsGasmaskUsed.Value = true;
    }

    private void OnDayStartServer()
    {
        if (!IsServer) return;
        // 아침에 5초 기다렸다가 판정
        StartCoroutine(EvaluatePendingInfectionAfterDelay(morningConversionDelay));
    }

    private IEnumerator EvaluatePendingInfectionAfterDelay(float delay)
    {
        yield return new WaitForSeconds(delay);
        EvaluatePendingInfectionServer();
    }

    public void EvaluatePendingInfectionServer()
    {
        if (!IsServer) return;

        bool infectedDuringNight = InfectedThisNight.Value;
        if (!infectedDuringNight) return;

        if (IsUnderProtection.Value)
        {
            IsInfected.Value = false;
            NotifyOwner("당신은 보호받아 감염되지 않았습니다.");
            return;
        }

        if (IsImmuneFromPharmacist.Value)
        {
            IsInfected.Value = false;
            NotifyOwner("약사에 의해 면역되었습니다. 감염이 해제됩니다.");
            return;
        }

        if (IsGasmaskEquipped.Value)
        {
            IsInfected.Value = false;
            IsGasmaskEquipped.Value = false;
            IsGasmaskUsed.Value = true;
            NotifyOwner("방독면이 작동하여 감염을 막았습니다. 방독면은 소모되었습니다.");
            return;
        }

        // 3) 실제 감염: 좀비로 변환
        // 찾을 플레이어의 NetworkObject (이 스크립트가 붙어있는 오브젝트의 NetworkObject)
        var netObj = GetComponent<NetworkObject>() ?? GetComponentInParent<NetworkObject>();
        if (netObj == null) return;
        

        Vector3 spawnPos = netObj.transform.position;
        Quaternion spawnRot = netObj.transform.rotation;

        if (zombieNetworkPrefab == null) return;

        var ph = netObj.GetComponent<PlayerHealth>() ?? netObj.GetComponentInChildren<PlayerHealth>() ?? netObj.GetComponentInParent<PlayerHealth>();
        if (ph != null)
        {
            // 서버에서 PlayerHealth에 변환 처리 위임 (이 함수은 서버에서 동작)
            ph.ConvertToZombieServer(zombieNetworkPrefab);
            // 전역 알림(선택): 모두에게 알림
            NotifyAllClientRpc($"{GetOwnerClientDisplayName()} 님이 감염되어 좀비가 되었습니다.");
        }
    }

    private ulong GetOwnerClientId()
    {
        var no = GetComponent<NetworkObject>() ?? GetComponentInParent<NetworkObject>();
        return no != null ? no.OwnerClientId : NetworkManager.Singleton.LocalClientId;
    }

    private string GetOwnerClientDisplayName()
    {
        ulong id = GetOwnerClientId();
        if (NetworkManager.Singleton == null) return $"Player{id}";
        if (NetworkManager.Singleton.ConnectedClients.TryGetValue(id, out var client))
        {
            var pm = client.PlayerObject?.GetComponent<PlayerMovement>();
            if (pm != null) return pm.playerName.Value.ToString();
        }
        return $"Player{GetOwnerClientId()}";
    }

    // 개인 알림 RPC (단일 클라이언트)
    [ClientRpc]
    private void NotifyClientRpc(string msg, ClientRpcParams rpcParams = default)
    {
        PersonalNotificationManager.Instance?.ShowPersonalMessage(msg);
    }

    // 전체 알림 RPC
    [ClientRpc]
    private void NotifyAllClientRpc(string msg, ClientRpcParams rpcParams = default)
    {
        PersonalNotificationManager.Instance?.ShowPersonalMessage(msg);
    }

    // 클래스 내부에 추가
    private void NotifyOwner(string msg)
    {
        if (NetworkManager.Singleton == null) return;
        ulong owner = GetOwnerClientId();
        var rpc = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { owner } } };
        NotifyAllClientRpc(msg, rpc);
    }

}
