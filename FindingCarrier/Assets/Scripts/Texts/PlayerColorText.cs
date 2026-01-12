using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public class PlayerColorText : NetworkBehaviour
{
    [Header("UI")]
    public TextMeshPro targetText;

    [Header("Player Info")]
    public PlayerMovement playerMovement; // 인스펙터에 연결

    private PlayerSpectatorManager spectatorManager;

    // 서버에서만 관리: 클라이언트ID -> 색상 인덱스
    private static Dictionary<ulong, int> colorAssignments = new Dictionary<ulong, int>();

    // 클라이언트 로컬: ownerClientId -> 해당 클라이언트의 PlayerColorText 인스턴스
    private static Dictionary<ulong, PlayerColorText> localRegistry = new Dictionary<ulong, PlayerColorText>();

    // RPC가 먼저 오고 인스턴스가 늦게 생성되는 경우를 위한 대기 저장소
    private struct PendingColor { public string name; public int idx; }
    private static Dictionary<ulong, PendingColor> pendingUpdates = new Dictionary<ulong, PendingColor>();

    // 색상 팔레트
    private static readonly string[] colors = new string[]
    {
        "red", "blue", "green", "#800080", "orange", "#A52A2A", "white", "yellow"
    };

    private void Awake()
    {
        spectatorManager = PlayerSpectatorManager.Instance;
    }

    private void Start()
    {
        if (targetText != null)
            targetText.richText = true;

        if (playerMovement != null)
            playerMovement.playerName.OnValueChanged += OnPlayerNameChanged;

        if (spectatorManager != null)
            spectatorManager.playerSlots.OnValueChanged += OnSlotsChanged;

        TryApplyInitialBySpectator();
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        spectatorManager = PlayerSpectatorManager.Instance;

        // **변경**: ownerId가 0이어도 등록(호스트의 경우 0일 수 있으므로)
        ulong ownerId = 0;
        if (playerMovement != null)
            ownerId = playerMovement.OwnerClientId;
        else if (NetworkObject != null)
            ownerId = NetworkObject.OwnerClientId;
        else if (NetworkManager.Singleton != null)
            ownerId = NetworkManager.Singleton.LocalClientId;

        // 등록 (항상 이 ownerId를 키로 사용)
        localRegistry[ownerId] = this;
        Debug.Log($"[PlayerColorText] Registered local instance for owner {ownerId} on client {NetworkManager.Singleton.LocalClientId}");

        // pending이 있으면 즉시 적용 (ownerId 기준)
        if (pendingUpdates.TryGetValue(ownerId, out var pending))
        {
            ApplyColorInternal(pending.name, pending.idx);
            pendingUpdates.Remove(ownerId);
            Debug.Log($"[PlayerColorText] Applied pending color for owner {ownerId} idx {pending.idx}");
        }

        if (IsServer)
        {
            NotifyColorServerRpc();
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;
        }
        else
        {
            if (IsOwner)
                RequestExistingPlayersColors();
        }
    }

    public override void OnDestroy()
    {
        if (playerMovement != null)
            playerMovement.playerName.OnValueChanged -= OnPlayerNameChanged;

        if (spectatorManager != null)
            spectatorManager.playerSlots.OnValueChanged -= OnSlotsChanged;

        // 레지스트리에서 제거 (ownerId는 NetworkObject.OwnerClientId 사용)
        ulong ownerId = NetworkObject != null ? NetworkObject.OwnerClientId : NetworkManager.Singleton.LocalClientId;
        if (localRegistry.ContainsKey(ownerId) && localRegistry[ownerId] == this)
        {
            localRegistry.Remove(ownerId);
            Debug.Log($"[PlayerColorText] Unregistered local instance for owner {ownerId}");
        }

        if (IsServer && NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
    }

    // 이벤트 시그니처들
    private void OnPlayerNameChanged(FixedString128Bytes previousValue, FixedString128Bytes newValue)
    {
        NotifyColorServerRpc();
    }

    private void OnSlotsChanged(PlayerSlotDataList previousValue, PlayerSlotDataList newValue)
    {
        TryApplyInitialBySpectator();
    }

    private void TryApplyInitialBySpectator()
    {
        if (spectatorManager == null || playerMovement == null || targetText == null) return;

        ulong ownerId = playerMovement.OwnerClientId;

        // 모든 슬롯을 슬롯 번호 기준으로 정렬 — 0번 슬롯도 포함
        var occupied = spectatorManager.playerSlots.Value.PlayerSlots
            .Where(s => s.ClientId != 0)    // 단, ClientId == 0(비사용자)만 제외
            .OrderBy(s => s.SlotNumber)
            .ToList();

        int index = occupied.FindIndex(s => s.ClientId == ownerId);

        // index가 -1이면 기본 0색 사용
        int clamped = Mathf.Clamp(index < 0 ? 0 : index, 0, colors.Length - 1);
        string colorHex = colors[clamped];
        targetText.text = $"<color={colorHex}>{RemoveRichTextTags(playerMovement.playerName.Value.ToString())}</color>";
    }


    private void RequestExistingPlayersColors()
    {
        if (!IsOwner) return;
        RequestAllColorsFromServerRpc();
    }

    // ---------------- Server RPCs ----------------

    [ServerRpc(RequireOwnership = false)]
    private void NotifyColorServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;

        ulong ownerId = playerMovement != null ? playerMovement.OwnerClientId : rpcParams.Receive.SenderClientId;
        string playerName = playerMovement != null ? playerMovement.playerName.Value.ToString() : $"Player{ownerId}";

        int idx = GetOrAssignColorIndex(ownerId);
        BroadcastColorClientRpc(ownerId, RemoveRichTextTags(playerName), idx);
        Debug.Log($"[Server] Broadcast color for owner {ownerId} idx {idx}");
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestAllColorsFromServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;

        ulong targetClientId = rpcParams.Receive.SenderClientId;

        foreach (var slot in spectatorManager.playerSlots.Value.PlayerSlots)
        {
            // 슬롯이 유효한 경우만 처리 (slot.ClientId 는 서버가 AddPlayer 할 때 세팅됨)
            if (slot.ClientId == 0) continue;

            ulong ownerClientId = slot.ClientId;
            string name = $"Player{ownerClientId}";

            // 있으면 이름을 얻어오고, 없으면 기본 이름 사용
            if (NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(slot.PlayerNetId, out var netObj) && netObj != null)
            {
                var movement = netObj.GetComponent<PlayerMovement>();
                if (movement != null)
                    name = movement.playerName.Value.ToString();
            }

            int idx = GetOrAssignColorIndex(ownerClientId);
            SendColorToClientClientRpc(ownerClientId, RemoveRichTextTags(name), targetClientId, idx);
            Debug.Log($"[Server] Sending color idx {idx} for owner {ownerClientId} to client {targetClientId}");
        }
    }

    // ---------------- Client RPCs ----------------

    [ClientRpc]
    private void SendColorToClientClientRpc(ulong ownerClientId, string playerName, ulong targetClientId, int colorIndex)
    {
        if (NetworkManager.Singleton.LocalClientId != targetClientId) return;

        // 레지스트리에서 찾으면 바로 적용, 없으면 pending으로 저장
        if (localRegistry.TryGetValue(ownerClientId, out var inst) && inst != null)
        {
            inst.ApplyColorInternal(playerName, colorIndex);
            Debug.Log($"[Client] SendColor applied to owner {ownerClientId} on client {NetworkManager.Singleton.LocalClientId}");
        }
        else
        {
            pendingUpdates[ownerClientId] = new PendingColor { name = playerName, idx = colorIndex };
            Debug.LogWarning($"[Client] SendColor received for owner {ownerClientId} but no local instance yet — queued pending");
        }
    }

    [ClientRpc]
    private void BroadcastColorClientRpc(ulong ownerClientId, string playerName, int colorIndex)
    {
        // 레지스트리에서 찾으면 바로 적용, 없으면 pending으로 저장
        if (localRegistry.TryGetValue(ownerClientId, out var inst) && inst != null)
        {
            inst.ApplyColorInternal(playerName, colorIndex);
            Debug.Log($"[Client] Broadcast applied to owner {ownerClientId} on client {NetworkManager.Singleton.LocalClientId}");
        }
        else
        {
            pendingUpdates[ownerClientId] = new PendingColor { name = playerName, idx = colorIndex };
            Debug.LogWarning($"[Client] Broadcast received for owner {ownerClientId} but no local instance yet — queued pending");
        }
    }

    // 실제로 텍스트를 갱신하는 내부 함수 (인스턴스 메서드)
    private void ApplyColorInternal(string playerName, int colorIndex)
    {
        if (targetText == null) return;

        int clamped = Mathf.Clamp(colorIndex, 0, colors.Length - 1);
        string colorHex = colors[clamped];
        targetText.text = $"<color={colorHex}>{playerName}</color>";
    }

    // --------------- 서버: 색상 인덱스 관리 ----------------

    private int GetOrAssignColorIndex(ulong clientId)
    {
        if (!IsServer) return 0;

        if (colorAssignments.TryGetValue(clientId, out int existing)) return existing;

        var used = new HashSet<int>(colorAssignments.Values);

        for (int i = 0; i < colors.Length; i++)
        {
            if (!used.Contains(i))
            {
                colorAssignments[clientId] = i;
                return i;
            }
        }

        int fallback = colorAssignments.Count % colors.Length;
        colorAssignments[clientId] = fallback;
        return fallback;
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (!IsServer) return;
        if (colorAssignments.ContainsKey(clientId))
            colorAssignments.Remove(clientId);
        Debug.Log($"[Server] Client {clientId} disconnected, freed color slot");
    }

    private string RemoveRichTextTags(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        int idx = 0;
        string result = "";
        while (idx < text.Length)
        {
            if (text[idx] == '<')
            {
                int end = text.IndexOf('>', idx);
                if (end >= 0) idx = end + 1;
                else break;
            }
            else
            {
                result += text[idx];
                idx++;
            }
        }
        return result;
    }
}
