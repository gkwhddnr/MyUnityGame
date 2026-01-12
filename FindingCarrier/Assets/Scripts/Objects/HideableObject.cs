using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

public class HideableObject : NetworkBehaviour, IInteractable
{
    [SerializeField] private Transform outsideInteractionPoint;
    [SerializeField] private Transform insideInteractionPoint;
    [SerializeField] private Transform hidePosition;
    [SerializeField] private Light hideableLight;
    private Light playerLight;

    public static readonly Dictionary<ulong, HideableObject> HideableByOwner = new Dictionary<ulong, HideableObject>();

    private NetworkObject currentCharacter; // 캐릭터 오브젝트 참조
    private NetworkObject currentContainer;

    public AudioSource hideableAudioSource;
    public AudioClip hideSound;
    public AudioClip unhideSound;

    public NetworkVariable<ulong> HiddenOwner = new NetworkVariable<ulong>(
        ulong.MaxValue,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public ulong CurrentPlayerOwnerId => currentCharacter != null ? currentCharacter.OwnerClientId : ulong.MaxValue;

    void Start()
    {
        hideableLight = GetComponent<Light>();
    }

    public override void OnNetworkSpawn()
    {
        HiddenOwner.OnValueChanged += OnHiddenOwnerChanged;

        // If a hide state already exists (late joiner), apply it locally
        if (HiddenOwner.Value != ulong.MaxValue)
        {
            // try to move the character locally to hidePosition and apply visuals
            ApplyExistingHiddenStateOnClient(HiddenOwner.Value);
        }
    }

    public override void OnNetworkDespawn()
    {
        HiddenOwner.OnValueChanged -= OnHiddenOwnerChanged;
    }

    private void OnHiddenOwnerChanged(ulong oldVal, ulong newVal)
    {
        if (newVal == ulong.MaxValue)
        {
            hideableLight.enabled = true;
        }
        else
        {
            hideableLight.enabled = true; // or enable point light depending design
            ApplyExistingHiddenStateOnClient(newVal);
        }
    }

    // Helper for late-joiners or clients receiving initial value
    private void ApplyExistingHiddenStateOnClient(ulong ownerClientId)
    {
        GameObject characterGO = FindCharacterByClientId(ownerClientId);
        if (characterGO == null) return;

        // 1) 컨테이너가 있으면 컨테이너를 hidePosition으로 이동
        var containerNetObj = characterGO.GetComponentInParent<NetworkObject>();
        if (containerNetObj != null && containerNetObj.gameObject != characterGO)
        {
            containerNetObj.transform.SetPositionAndRotation(hidePosition.position, hidePosition.rotation);
            // 자식 캐릭터는 로컬에서 렌더/라이트 숨김
            SetAllRenderersEnabled(characterGO, false);
        }
        else
        {
            // 캐릭터 직접 이동
            characterGO.transform.SetPositionAndRotation(hidePosition.position, hidePosition.rotation);
            SetAllRenderersEnabled(characterGO, false);
        }

        // 만약 이 로컬 클라이언트가 숨은 플레이어의 소유자라면 시야/라이트 조정
        if (NetworkManager.Singleton.LocalClientId == ownerClientId)
        {
            GetComponent<VisionController>()?.SwitchToPointView();
            var pLight = characterGO.GetComponentInChildren<Light>();
            if (pLight != null) pLight.enabled = false;
        }
    }

    private void Update()
    {
        if (!IsServer) return; // 서버에서만 처리
    }

    [ServerRpc(RequireOwnership = false)]
    public void InteractServerRpc(ulong playerId, ServerRpcParams rpcParams = default)
    {
        if (!NetworkManager.Singleton.ConnectedClients.TryGetValue(playerId, out var connected)) return;

        // 1) PlayerCharacterManager를 통해 clientId -> character GameObject를 바로 얻음
        GameObject characterGO = FindCharacterByClientId(playerId);

        if (characterGO == null)
        {
            Debug.LogWarning($"[Hideable] Could not find character GameObject for client {playerId}");
            return;
        }

        // 이제 characterGO 기준으로 거리 계산과 숨김/나오기 로직 수행
        var characterNetObj = characterGO.GetComponent<NetworkObject>();
        float outsideDistance = Vector3.Distance(characterGO.transform.position, outsideInteractionPoint.position);
        float insideDistance = Vector3.Distance(characterGO.transform.position, insideInteractionPoint.position);

        Debug.Log($"[HideableObject] Player {playerId} distances - Outside: {outsideDistance}, Inside: {insideDistance}, CurrentPlayer: {(currentCharacter != null ? currentCharacter.OwnerClientId.ToString() : "null")}");

        if (currentCharacter == null && outsideDistance < 1.5f)
        {
            HandleHide(characterNetObj);
        }
        else if (currentCharacter == characterNetObj && insideDistance < 1.5f)
        {
            HandleUnhide(characterNetObj);
        }
        else if (currentCharacter != null && currentCharacter != characterNetObj)
        {
            ShowNotificationClientRpc(playerId, "누군가 이미 숨어있습니다.");
            return;
        }
    }

    private GameObject FindCharacterByClientId(ulong clientId)
    {
        // 1) PlayerCharacterManager를 통해 찾기
        var pcm = FindFirstObjectByType<PlayerCharacterManager>();
        if (pcm != null)
        {
            GameObject characterGO = pcm.GetCharacterByClientId(clientId);
            if (characterGO != null) return characterGO;
        }

        // 2) 폴백: SpawnedObjects 검색 (캐릭터만)
        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var netObj = kv.Value;
            if (netObj == null) continue;
            if (netObj.OwnerClientId == clientId)
            {
                // PlayerMovement가 있고 IsCharacterInstance()가 true인 경우만
                var playerMovement = netObj.GetComponent<PlayerMovement>();
                if (playerMovement != null && playerMovement.IsCharacterInstance())
                {
                    return netObj.gameObject;
                }
            }
        }

        return null;
    }

    private void HandleHide(NetworkObject characterNetObj)
    {
        if (currentCharacter != null && currentCharacter == characterNetObj) return;

        GameObject characterGO = characterNetObj.gameObject;
        playerLight = characterGO.GetComponentInChildren<Light>();

        var containerNetObj = characterGO.GetComponentInParent<NetworkObject>();
        if (containerNetObj != null && containerNetObj != characterNetObj)
        {
            currentContainer = containerNetObj;
        }
        else
        {
            currentContainer = null;
        }

        // 1) 물리: 먼저 velocity 0으로 만들고 그 다음 isKinematic 변경 (경고 방지)
        // 적용대상: 컨테이너 우선, 없으면 캐릭터
        var physTarget = (currentContainer != null) ? currentContainer.gameObject : characterGO;

        var rb = physTarget.GetComponent<Rigidbody>();
        if (rb != null)
        {
            // 먼저 속도 제거 (only when currently non-kinematic)
#if UNITY_2023_2_OR_NEWER
            if (!rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
#else
    rb.velocity = Vector3.zero;
    rb.angularVelocity = Vector3.zero;
#endif
            // 그 다음 kinematic으로 전환
            rb.isKinematic = true;
        }

        // 2) 콜라이더를 trigger로 바꿔 물리 간섭 방지
        SetAllCollidersAsTrigger(physTarget, true);

        // 3) 서버에서 실제 transform을 이동 (컨테이너가 있으면 컨테이너 기준으로 이동)
        if (currentContainer != null)
        {
            currentContainer.transform.SetPositionAndRotation(hidePosition.position, hidePosition.rotation);
        }
        else
        {
            characterGO.transform.SetPositionAndRotation(hidePosition.position, hidePosition.rotation);
        }

        currentCharacter = characterNetObj;

        if (IsServer) HideableByOwner[characterNetObj.OwnerClientId] = this;

        // IMPORTANT: networked hidden state
        HiddenOwner.Value = characterNetObj.OwnerClientId;

        // 4) 모든 클라이언트에게 위치 동기화: 컨테이너 + character 모두 전달 (클라이언트 구조 상 안전)
        if (currentContainer != null)
        {
            MovePlayerClientRpc(currentContainer.NetworkObjectId, hidePosition.position, hidePosition.rotation);
        }
        MovePlayerClientRpc(characterNetObj.NetworkObjectId, hidePosition.position, hidePosition.rotation);

        // 5) Owner 전용 로컬 처리: 움직임 차단, 시야 변경, 라이트/오디오
        SetHideClientRpc(characterNetObj.NetworkObjectId);

        var clientParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { characterNetObj.OwnerClientId } } };
        // 6) 모든 클라이언트에서 렌더러를 숨김 (모든 하위 렌더)
        TogglePlayerRendererClientRpc(characterNetObj.OwnerClientId, true, clientParams);

        // 7) Play sound (owner only)
        PlayHideSoundClientRpc(clientParams);

        // 8) Apply owner vision/light
        ApplyVisionAndLightHideClientRpc(characterNetObj.OwnerClientId);

        Debug.Log($"[서버] {characterGO.name} 숨음 (owner:{characterNetObj.OwnerClientId})");
    }

    private void HandleUnhide(NetworkObject characterNetObj)
    {
        if (characterNetObj == null) return;

        GameObject characterGO = characterNetObj.gameObject;
        playerLight = characterGO.GetComponentInChildren<Light>();

        // 컨테이너 재탐색 (현재Container가 남아있지 않을 수 있음)
        var containerNetObj = characterGO.GetComponentInParent<NetworkObject>();
        if (containerNetObj != null && containerNetObj != characterNetObj)
            currentContainer = containerNetObj;
        else
            currentContainer = null;

        Vector3 targetPos = outsideInteractionPoint.position;
        Quaternion targetRot = outsideInteractionPoint.rotation;

        // 1) 물리: 컨테이너 우선
        var physTarget = (currentContainer != null) ? currentContainer.gameObject : characterGO;
        var rb = physTarget.GetComponent<Rigidbody>();
        if (rb != null)
        {
            // kinematic 풀기 먼저
            rb.isKinematic = false;

            // 안전하게 속도 초기화 (이제 non-kinematic이므로 linearVelocity 설정 가능)
#if UNITY_2023_2_OR_NEWER
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
#else
    rb.velocity = Vector3.zero;
    rb.angularVelocity = Vector3.zero;
#endif
        }

        // 2) 콜라이더 복구 (isTrigger -> false)
        SetAllCollidersAsTrigger(physTarget, false);

        // 3) 위치 복구 (컨테이너/캐릭터 둘 다 동기화)
        if (currentContainer != null)
        {
            currentContainer.transform.SetPositionAndRotation(targetPos, targetRot);
        }
        characterGO.transform.SetPositionAndRotation(targetPos, targetRot);

        // 서버에서 HideableByOwner 정리
        if (IsServer)
        {
            if (HideableByOwner.ContainsKey(characterNetObj.OwnerClientId) && HideableByOwner[characterNetObj.OwnerClientId] == this)
                HideableByOwner.Remove(characterNetObj.OwnerClientId);
        }

        currentCharacter = null;
        currentContainer = null;

        // Clear networked hide state
        HiddenOwner.Value = ulong.MaxValue;

        // Broadcast move to clients
        if (currentContainer != null)
        {
            MovePlayerClientRpc(currentContainer.NetworkObjectId, targetPos, targetRot);
        }
        MovePlayerClientRpc(characterNetObj.NetworkObjectId, targetPos, targetRot);

        // Owner local unhide
        SetUnhideClientRpc(characterNetObj.NetworkObjectId);

        var clientParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { characterNetObj.OwnerClientId } } };
        // 모든 클라이언트: 렌더 보이기
        TogglePlayerRendererClientRpc(characterNetObj.OwnerClientId, false, clientParams);

        PlayUnhideSoundClientRpc(clientParams);

        ApplyVisionAndLightUnhideClientRpc(characterNetObj.OwnerClientId);

        Debug.Log($"[서버] {characterGO.name} 나옴 (owner:{characterNetObj.OwnerClientId})");
    }

    public void ForceUnhideServer(NetworkObject playerNetObj)
    {
        if (!IsServer) return;
        if (playerNetObj == null) return;

        // 만약 현재 이 오브젝트에 같은 플레이어가 있으면 정리
        if (currentCharacter != null && currentCharacter == playerNetObj)
        {
            currentCharacter = null;

            if (HideableByOwner.ContainsKey(playerNetObj.OwnerClientId) && HideableByOwner[playerNetObj.OwnerClientId] == this)
                HideableByOwner.Remove(playerNetObj.OwnerClientId);
        }
    }

    [ClientRpc]
    private void PlayHideSoundClientRpc(ClientRpcParams rpcParams = default)
    {
        if (hideableAudioSource != null && hideSound != null)
        {
            hideableAudioSource.PlayOneShot(hideSound);
        }
    }

    [ClientRpc]
    private void PlayUnhideSoundClientRpc(ClientRpcParams rpcParams = default)
    {
        if (hideableAudioSource != null && unhideSound != null)
        {
            hideableAudioSource.PlayOneShot(unhideSound);
        }
    }

    [ClientRpc]
    private void SetHideClientRpc(ulong characterNetworkId, ClientRpcParams rpcParams = default)
    {
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(characterNetworkId, out var charNetObj)) return;

        // Only the owner client should block their own movement locally
        if (NetworkManager.Singleton.LocalClientId != charNetObj.OwnerClientId) return;

        var movement = charNetObj.GetComponent<PlayerMovement>();
        if (movement != null && movement.IsCharacterInstance())
        {
            movement.SetCanMoveLocal(false);
            movement.ForceStopWalkingLocal();
        }

        // 로컬에서는 모든 렌더 숨김 (안전)
        SetAllRenderersEnabled(charNetObj.gameObject, false);
    }

    [ClientRpc]
    public void SetUnhideClientRpc(ulong characterNetworkId, ClientRpcParams rpcParams = default)
    {
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(characterNetworkId, out var charNetObj)) return;

        // Only the owner client should restore their own movement locally
        if (NetworkManager.Singleton.LocalClientId != charNetObj.OwnerClientId) return;

        var movement = charNetObj.GetComponent<PlayerMovement>();
        if (movement != null && movement.IsCharacterInstance())
        {
            movement.SetCanMoveLocal(true);
        }

        SetAllRenderersEnabled(charNetObj.gameObject, true);

        // Apply vision/light on the owner client
        ApplyVisionAndLightUnhideClientRpc(charNetObj.OwnerClientId);
    }

    [ClientRpc]
    private void ShowNotificationClientRpc(ulong targetClientId, string message)
    {
        if (NetworkManager.Singleton.LocalClientId != targetClientId) return;

        var personalUI = PersonalNotificationManager.Instance ?? FindFirstObjectByType<PersonalNotificationManager>(FindObjectsInactive.Include);
        personalUI?.ShowPersonalMessage(message);
    }

    [ClientRpc]
    private void MovePlayerClientRpc(ulong characterNetworkId, Vector3 targetPosition, Quaternion targetRotation, ClientRpcParams rpcParams = default)
    {
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(characterNetworkId, out var charNetObj)) return;

        var character = charNetObj.gameObject;
        character.transform.SetPositionAndRotation(targetPosition, targetRotation);

        var rb = character.GetComponent<Rigidbody>();
        if (rb != null)
        {
            // position 바로 세팅할 경우, kinematic 상태를 고려
            rb.isKinematic = true;
            rb.position = targetPosition; // 또는 transform.SetPositionAndRotation
            rb.rotation = targetRotation;
            // 비활성시 속도는 안건드려도 되지만 안전을 위해:
#if UNITY_2023_2_OR_NEWER
            if (!rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
#else
    rb.velocity = Vector3.zero;
    rb.angularVelocity = Vector3.zero;
#endif
        }
    }

    [ClientRpc]
    private void ApplyVisionAndLightHideClientRpc(ulong targetClientId)
    {
        if (NetworkManager.Singleton.LocalClientId != targetClientId) return;
        
        // 내 시야를 Point로
        GetComponent<VisionController>()?.SwitchToPointView();

        // 플레이어 자신의 캐릭터 Light 비활성화
        GameObject characterGO = FindCharacterByClientId(targetClientId);
        if (characterGO != null)
        {
            var pLight = characterGO.GetComponentInChildren<Light>();
            if (pLight != null) pLight.enabled = false;
        }

        // 은신 오브젝트 라이트 켜기 (내 화면만)
        hideableLight.type = LightType.Point;
        hideableLight.enabled = true;
    }

    [ClientRpc]
    public void ApplyVisionAndLightUnhideClientRpc(ulong targetClientId)
    {
        if (NetworkManager.Singleton.LocalClientId != targetClientId) return;
        
        // 내 시야를 Spot으로
        GetComponent<VisionController>()?.SwitchToNormalView();

        // 플레이어 자신의 캐릭터 Light 활성화
        GameObject characterGO = FindCharacterByClientId(targetClientId);
        if (characterGO != null)
        {
            var pLight = characterGO.GetComponentInChildren<Light>();
            if (pLight != null) pLight.enabled = true;
        }

        // 은신 오브젝트 라이트 끄기
        hideableLight.type = LightType.Spot;
        hideableLight.enabled = false;
    }

    // 플레이어 렌더러 토글 RPC
    [ClientRpc]
    public void TogglePlayerRendererClientRpc(ulong targetClientId, bool isHidden, ClientRpcParams rpcParams = default)
    {
        if (NetworkManager.Singleton == null) return;
        if (NetworkManager.Singleton.LocalClientId != targetClientId) return;

        GameObject characterGO = FindCharacterByClientId(targetClientId);
        if (characterGO == null) return;

        SetAllRenderersEnabled(characterGO, !isHidden);
    }

    // 밤이 되어 숨어 있는 플레이어의 숨는 오브젝트 라이트 끄기
    [ClientRpc]
    public void SetNightHideableLightClientRpc(ClientRpcParams rpcParams = default)
    {
        hideableLight.enabled = false;
    }

    // 낮이 되어 다시 켜기
    [ClientRpc]
    public void SetDayHideableLightClientRpc(ClientRpcParams rpcParams = default)
    {
        hideableLight.enabled = true;
    }

    private void SetAllRenderersEnabled(GameObject go, bool enabled)
    {
        if (go == null) return;

        // 모든 하위 렌더러 수집
        var rends = go.GetComponentsInChildren<Renderer>(true);
        foreach (var r in rends)
        {
            if (r == null) continue;

            var tmpMesh = r.GetComponent<TMPro.TextMeshPro>();
            if (tmpMesh != null)
            {
                // 이름 텍스트(또는 다른 TMP 메시 타입)는 숨기지 않음
                continue;
            }

            r.enabled = enabled;
        }
    }

    // 모든 하위 Collider를 trigger 모드로 변경(숨김 시 true), 복구 시 false
    private void SetAllCollidersAsTrigger(GameObject go, bool makeTrigger)
    {
        var cols = go.GetComponentsInChildren<Collider>(true);
        foreach (var c in cols)
        {
            c.isTrigger = makeTrigger;
        }
    }
}