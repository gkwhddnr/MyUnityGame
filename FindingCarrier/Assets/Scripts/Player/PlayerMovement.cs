using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public partial class PlayerMovement : NetworkBehaviour
{
    [SerializeField] private float moveSpeed = 15f;
    [SerializeField] private Light playerLight;
    private Rigidbody rb;
    private bool canMove = true;
    private Animator animator;

    [Header("Player Name")]
    public string playerInitialName; // 로그인 시 이름을 여기에 할당

    [Header("Player Name UI")]
    public TextMeshPro nameText;

    public AudioSource personalAudioSource;
    public AudioSource personalAudioSource2;

    [Header("Footstep Sounds")]
    public AudioClip[] footstepSounds; // 발소리 클립 배열
    public AudioClip[] grassFootstepSounds;       // 잔디 위 발소리
    [SerializeField] private float footstepDelay = 0.5f; // 발소리 간격 (초)
    private float footstepTimer; // 발소리 재생을 위한 타이머

    [Header("Ground Check")]
    [SerializeField] private float groundCheckDistance = 1.1f;
    private bool isOnGrass = false;


    private NetworkVariable<bool> isWalking = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public NetworkVariable<FixedString128Bytes> playerName = new NetworkVariable<FixedString128Bytes>(
    "",  // 기본값
    NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Server
    );

    public NetworkVariable<bool> CanMoveVar = new NetworkVariable<bool>(
        true,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    public NetworkVariable<int> RoomId = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<bool> isDead = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    public NetworkVariable<float> SpeedMultiplier = new NetworkVariable<float>(
    1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<FixedString128Bytes> AssignedRole = new NetworkVariable<FixedString128Bytes>(
    new FixedString128Bytes(""), NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // Container와 Character 구분을 위한 참조
    private NetworkObject containerNetObj;
    private bool isCharacterInstance = false;

    private bool hasCarrierComponent = false;
    private bool isNightTime = false;
    public bool NetworkCanMove => CanMoveVar.Value;


    public void SetAssignedRoleServer(string role)
    {
        if (!IsServer) return;
        AssignedRole.Value = new FixedString128Bytes(role);
    }

    private void OnEnable()
    {
        if (DayNightManager.Instance != null)
        {
            DayNightManager.Instance.onNightStart.AddListener(OnNightStart);
            DayNightManager.Instance.onDayStart.AddListener(OnDayStart);
        }
    }

    private void OnDisable()
    {
        if (DayNightManager.Instance != null)
        {
            DayNightManager.Instance.onNightStart.RemoveListener(OnNightStart);
            DayNightManager.Instance.onDayStart.RemoveListener(OnDayStart);
        }
    }

    private void Awake()
    {
        animator = GetComponent<Animator>();
        rb = GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.useGravity = true;
            rb.freezeRotation = true;
        }

        // Container인지 Character인지 확인
        isCharacterInstance = gameObject.name.Contains("Character") || animator != null;
        
        if (IsLocalPlayer && isCharacterInstance)
        {
            if (playerLight != null)
            {
                playerLight.gameObject.layer = LayerMask.NameToLayer("PlayerLight");
                Camera.main.cullingMask |= 1 << LayerMask.NameToLayer("PlayerLight");
            }
        }
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        
        // Container가 아닌 실제 캐릭터인 경우에만 물리 설정
        if (isCharacterInstance && rb != null)
        {
            rb.isKinematic = false;
        }

        if(IsServer && !isCharacterInstance) 
            UIScreenTransitionManager.Instance.RegisterPlayer(gameObject);

        if (IsServer && isCharacterInstance)
        {
            bool isCarrierRole = DetectCarrierRole();
            if (isCarrierRole)
            {
                CanMoveVar.Value = true;
            }
        }

        playerName.OnValueChanged += OnPlayerNameChanged;
        isWalking.OnValueChanged += OnIsWalkingChanged;

        // Container를 찾아서 참조 저장
        if (isCharacterInstance)
        {
            FindContainerReference();
            CheckCarrierComponent();
        }

        // 값이 바뀔 때마다 한 번만 호출됨
        // 기존 람다 대신 메서드 호출로 대체
        CanMoveVar.OnValueChanged += OnCanMoveVarChanged;


        if (IsOwner)
        {
            string nameFromUI = null;
            try
            {
                nameFromUI = UIScreenTransitionManager.Instance?.inputField?.text;
            }
            catch
            {
                nameFromUI = null;
            }

            if (!string.IsNullOrWhiteSpace(nameFromUI))
            {
                // 로컬 이름이 있으면 서버에 설정 요청
                SetPlayerNameServerRpc(nameFromUI);
            }
            else if (!string.IsNullOrWhiteSpace(playerInitialName))
            {
                // 초기값이 있으면 서버에 설정
                SetPlayerNameServerRpc(playerInitialName);
            }
        }

        // 2) 서버: playerName이 비어있으면 기본값 설정
        if (IsServer)
        {
            if (string.IsNullOrWhiteSpace(playerName.Value.ToString()))
            {
                string defaultName = IsOwner ? playerInitialName : $"Player{OwnerClientId}";
                playerName.Value = new FixedString128Bytes(defaultName);
            }
        }

        // 모든 클라이언트가 호출: UI 업데이트
        UpdateAllPlayersUI();


        // UI 초기화
        if (nameText != null) nameText.text = playerName.Value.ToString();
        if (IsOwner && !isCharacterInstance)
        {
            StartCoroutine(RestorePasswordUIAfterPlayerSpawn());
        }

        if (IsCharacterInstance())
        {
            // 슬롯 정보 가져오기 (있으면 색 적용)
            int slotNum = 0;
            var ps = FindFirstObjectByType<PlayerSpectatorManager>();
            if (ps != null)
            {
                var list = ps.playerSlots.Value.PlayerSlots;
                if (list != null)
                {
                    var s = list.Find(x => x.ClientId == OwnerClientId);
                    if (s.SlotNumber != 0) slotNum = s.SlotNumber;
                }
            }

            // playerName이 NetworkVariable에 이미 채워져 있을 수 있으니 사용
            string display = playerName.Value.ToString();
            // NameplateManager에 등록 (instance may be null if manager not created yet)
            if (NameplateManager.Instance != null)
                NameplateManager.Instance.RegisterPlayer(OwnerClientId, transform, display, slotNum);
            else
                StartCoroutine(WaitAndRegisterNameplate(display, slotNum));
        }
        if (IsServer) SpeedMultiplier.Value = 1f;

        if (DayNightManager.Instance != null)
        {
            // 현재 밤/낮 상태 확인
            isNightTime = DayNightManager.Instance.isNight.Value;
            UpdateMovementBasedOnNightAndCarrier();
        }
    }
    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        if (IsServer && !isCharacterInstance)
            UIScreenTransitionManager.Instance.UnregisterPlayer(gameObject);
        if (IsCharacterInstance())
        {
            if (NameplateManager.Instance != null)
                NameplateManager.Instance.UnregisterPlayer(OwnerClientId);
        }
        playerName.OnValueChanged -= OnPlayerNameChanged;
        isWalking.OnValueChanged -= OnIsWalkingChanged;
        CanMoveVar.OnValueChanged -= OnCanMoveVarChanged; // <- 추가 (중복 등록 방지)
    }

    private void CheckCarrierComponent()
    {
        if (!isCharacterInstance) return;

        // 현재 캐릭터에서 carrier 컴포넌트 확인
        var carrierComp = GetComponent<carrier>();
        hasCarrierComponent = (carrierComp != null);

        // Container에서도 확인 (혹시 Container에 붙어있을 수 있음)
        if (!hasCarrierComponent && containerNetObj != null)
        {
            var containerCarrier = containerNetObj.GetComponent<carrier>();
            hasCarrierComponent = (containerCarrier != null);
        }

        Debug.Log($"[PlayerMovement] Player {OwnerClientId} hasCarrierComponent: {hasCarrierComponent}");
    }

    public void ServerSetCanMove(bool value)
    {
        if (!IsServer) return;
        CanMoveVar.Value = value;

        bool isCarrier = DetectCarrierRole();
        if (!value && isCarrier)
        {
            Debug.Log($"[PlayerMovement] Ignored ServerSetCanMove(false) for Owner {OwnerClientId} because they are Carrier.");
            // 보균자는 항상 이동 가능하도록 유지
            CanMoveVar.Value = true; // internalCanMoveVar 또는 기존 CanMoveVar.Value (파일에서 사용중인 네임으로 맞춰줘)
            return;
        }

        // 정상적으로 설정
        CanMoveVar.Value = value;
        Debug.Log($"[PlayerMovement] ServerSetCanMove -> Owner:{OwnerClientId} value:{value} (carrier:{isCarrier})");
    }

    public bool IsCarrierRoleServer()
    {
        if (!IsServer) return false;
        return DetectCarrierRole();
    }

    private IEnumerator WaitAndRegisterNameplate(string display, int slot)
    {
        float t = 0f;
        while (NameplateManager.Instance == null && t < 2f)
        {
            t += Time.deltaTime;
            yield return null;
        }
        if (NameplateManager.Instance != null)
            NameplateManager.Instance.RegisterPlayer(OwnerClientId, transform, display, slot);
    }

    private void OnNightStart()
    {
        Debug.Log($"[PlayerMovement] Night started for player {OwnerClientId}");
        UpdateMovementBasedOnNightAndCarrier();
    }

    // Carrier 스크립트에서 사용하는 낮 시작 이벤트 핸들러
    private void OnDayStart()
    {
        Debug.Log($"[PlayerMovement] Day started for player {OwnerClientId}");
        UpdateMovementBasedOnNightAndCarrier();
    }

    private bool DetectCarrierRole()
    {
        if (NetworkManager.Singleton == null) return false;

        int carrierLayerIndex = LayerMask.NameToLayer("Carrier");

        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var no = kv.Value;
            if (no == null) continue;
            if (no.OwnerClientId != OwnerClientId) continue;

            // 1) 레이어로 판정 (인스펙터에서 SetLayerRecursive로 변경했을 때 유효)
            if (carrierLayerIndex >= 0 && no.gameObject.layer == carrierLayerIndex)
                return true;

            // 2) carrier 컴포넌트가 붙어 있고 네트워크변수로 활성화되어 있으면 판정
            var carrierComp = no.GetComponent<carrier>() ?? no.GetComponentInChildren<carrier>() ?? no.GetComponentInParent<carrier>();
            if (carrierComp != null && carrierComp.IsCarrier.Value)
                return true;
        }

        return false;
    }

    private void UpdateMovementBasedOnNightAndCarrier()
    {
        if (!IsServer || !isCharacterInstance) return;

        bool isCurrentlyNight = DayNightManager.Instance != null && DayNightManager.Instance.isNight.Value;

        // 1) Carrier 역할인지 판별: (A) 오브젝트/컨테이너 레이어가 Carrier 이거나 (B) carrier 컴포넌트가 있고 IsCarrier == true
        bool isCarrierRole = false;
        int carrierLayerIndex = LayerMask.NameToLayer("Carrier");

        // A: container 레이어 검사 (이미 저장된 containerNetObj 참조가 있으면 우선 검사)
        if (containerNetObj != null && carrierLayerIndex >= 0 && containerNetObj.gameObject.layer == carrierLayerIndex)
        {
            isCarrierRole = true;
        }
        else
        {
            // B: SpawnedObjects 내에서 소유 오브젝트들 검사 (container 또는 character 어느 쪽이라도)
            if (NetworkManager.Singleton != null)
            {
                foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
                {
                    var no = kv.Value;
                    if (no == null) continue;
                    if (no.OwnerClientId != OwnerClientId) continue;

                    // 레이어가 Carrier면 바로 Carrier로 간주
                    if (carrierLayerIndex >= 0 && no.gameObject.layer == carrierLayerIndex)
                    {
                        isCarrierRole = true;
                        break;
                    }

                    // carrier 컴포넌트 여부 검사
                    var carrierComp = no.GetComponent<carrier>() ?? no.GetComponentInChildren<carrier>() ?? no.GetComponentInParent<carrier>();
                    if (carrierComp != null && carrierComp.IsCarrier.Value)
                    {
                        isCarrierRole = true;
                        break;
                    }
                }
            }
        }

        if (isCarrierRole)
        {
            CanMoveVar.Value = true;
            Debug.Log($"[PlayerMovement] Owner {OwnerClientId} is carrier => can move regardless of day/night");
        }
        else
        {
            if (!isCurrentlyNight)
            {
                CanMoveVar.Value = true;
                Debug.Log($"[PlayerMovement] Day time - Player {OwnerClientId} can move");
            }
            else
            {
                CanMoveVar.Value = false;
                Debug.Log($"[PlayerMovement] Night time - Player {OwnerClientId} cannot move (not carrier)");
            }
        }
    }


    // CanMoveVar 변경 처리 콜백 (서버/클라이언트에서 모두 호출됨)
    private void OnCanMoveVarChanged(bool oldVal, bool newVal)
    {
        // 로컬 변수 및 물리 상태 반영 (기존 동작 유지)
        canMove = newVal;
        if (isCharacterInstance && rb != null)
        {
            rb.isKinematic = !newVal;
        }
        Debug.Log($"[PlayerMovement] Player {OwnerClientId} canMove: {newVal}, isCharacter: {isCharacterInstance}");


        if (IsServer && !newVal)
        {
            ForceStopWalkingClientRpc();
        }
    }

    private void FindContainerReference()
    {
        // 캐릭터에서 Container 찾기
        foreach (var kvp in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var netObj = kvp.Value;
            if (netObj.OwnerClientId == OwnerClientId && 
                netObj.gameObject != gameObject &&
                netObj.gameObject.name.Contains("Container"))
            {
                containerNetObj = netObj;
                Debug.Log($"[PlayerMovement] Character found container: {containerNetObj.gameObject.name}");
                break;
            }
        }
    }

    private IEnumerator RestorePasswordUIAfterPlayerSpawn()
    {
        yield return new WaitForSeconds(4f); // 네트워크 안정화 대기

        var ui = TogglePasswordUI.Instance;
        if (ui != null && ui.IsCurrentlyHost())
        {
            ui.RestoreStateAfterNetworkReconnect();
        }
    }

    [ServerRpc]
    public void SetPlayerNameServerRpc(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        playerName.Value = new FixedString128Bytes(newName);
    }

    private void OnPlayerNameChanged(FixedString128Bytes oldName, FixedString128Bytes newName)
    {
        string nameStr = newName.ToString();

        // 1) nameText가 붙어있다면 바로 갱신
        if (nameText != null)
        {
            nameText.text = nameStr;
        }

        // 2) 전체 플레이어 리스트 UI가 있다면 갱신 (ex: 로비 UI)
        UpdateAllPlayersUI();
    }

    private void OnIsWalkingChanged(bool oldValue, bool newValue)
    {
        // 모든 클라이언트에서 Animator의 "IsWalking" 파라미터를 업데이트합니다.
        // 이것이 바로 동기화의 핵심입니다.
        if (animator != null)
        {
            animator.SetBool("IsWalking", newValue);
        }
    }

    private void UpdateAllPlayersUI()
    {
        // UI 업데이트 함수 호출
        if (UIScreenTransitionManager.Instance != null)
        {
            UIScreenTransitionManager.Instance.UpdateAllPlayerNamesInUI();
        }
    }

    // Update is called once per frame
    void Update()
    {
        // 캐릭터 인스턴스에서만 이동 처리
        if(!IsOwner || !canMove || !isCharacterInstance) return;

        Move();
        UpdateLightDirection();
        UpdateAnimation();

        CheckGroundTag();
        UpdateFootstepSounds();
    }

    private void CheckGroundTag()
    {
        if (!isCharacterInstance) return;
        
        // 플레이어 발 위치에서 바닥 방향으로 레이캐스트
        Ray ray = new Ray(transform.position + Vector3.up * 0.1f, Vector3.down);
        if (Physics.Raycast(ray, out RaycastHit hit, groundCheckDistance))
        {
            isOnGrass = hit.collider.CompareTag("grass");
        }
        else
        {
            isOnGrass = false;
        }
    }

    private void UpdateFootstepSounds()
    {
        if (!isCharacterInstance) return;
        
        // 발소리 재생 조건
        if (!IsOwner || !isWalking.Value) return;

        footstepTimer -= Time.deltaTime;
        if (footstepTimer > 0f) return;

        // 현재 지면에 맞는 클립 배열 선택
        AudioClip[] clipsToUse = (isOnGrass && grassFootstepSounds != null && grassFootstepSounds.Length > 0)
            ? grassFootstepSounds
            : footstepSounds;

        if (clipsToUse == null || clipsToUse.Length == 0) return;

        // 랜덤 선택 후 재생
        int idx = Random.Range(0, clipsToUse.Length);
        personalAudioSource2.clip = clipsToUse[idx];
        personalAudioSource2.Play();

        footstepTimer = footstepDelay;
    }

    void UpdateAnimation()
    {
        if (!isCharacterInstance) return;
        
        // 입력값을 기반으로 움직이는지 판단
        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        bool isMoving = (Mathf.Abs(h) > 0.1f || Mathf.Abs(v) > 0.1f);
        if (isWalking.Value != isMoving) isWalking.Value = isMoving;
    }

    void UpdateLightDirection()
    {
        if (!isCharacterInstance || animator == null) return;

        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");

        Vector3 direction = new Vector3(h, 0f, v).normalized;
        if (direction.sqrMagnitude > 0.01f && playerLight != null)
        {
            // 바라보는 방향으로 조명 회전
            Quaternion lookRotation = Quaternion.LookRotation(direction);
            playerLight.transform.rotation = lookRotation;
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void SetSpeedMultiplierServerRpc(float multiplier, ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;
        // 유효성 검사 (예: 0.1 ~ 4.0 범위)
        multiplier = Mathf.Clamp(multiplier, 0.1f, 4f);
        SpeedMultiplier.Value = multiplier;
    }

    public void SetCanMoveLocal(bool allow)
    {
        canMove = allow;
        if (rb == null) return;

        if (!allow)
        {
            // 움직임 차단: 비물리 속도 먼저 제거 -> 그 다음 kinematic으로 설정
            // NOTE: Unity asks to use linearVelocity; only set linearVelocity when rb is non-kinematic.
#if UNITY_2023_2_OR_NEWER
            // If linearVelocity exists in your Unity version, set it only if not kinematic
            if (!rb.isKinematic)
            {
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
            }
#else
        // For older APIs use velocity
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
#endif
            rb.isKinematic = true;
        }
        else
        {
            // 움직임 허용: kinematic 해제 먼저
            rb.isKinematic = false;
            // 속도는 보통 0으로 남겨둬도 괜찮음. 필요하면 여기서 초기화
#if UNITY_2023_2_OR_NEWER
            // linearVelocity OK because we just set isKinematic = false
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
#else
        rb.velocity = Vector3.zero;
        rb.angularVelocity = Vector3.zero;
#endif
        }
    }

    public bool getCanMove()
    {
        return canMove;
    }

    void Move()
    {
        if (!isCharacterInstance || rb == null) return;
        
        float h = Input.GetAxis("Horizontal");
        float v = Input.GetAxis("Vertical");
        Vector3 direction = new Vector3(h, 0, v).normalized;

        float multiplier = 1f;
        if (SpeedMultiplier != null) multiplier = SpeedMultiplier.Value;

        rb.MovePosition(transform.position + direction * moveSpeed* Time.deltaTime);
    }

    [ServerRpc(RequireOwnership = false)]
    public void SubmitMessageServerRpc(string message, ServerRpcParams rpcParams = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        ulong senderId = rpcParams.Receive.SenderClientId;

        // Container의 PlayerMovement에서 사망 상태 확인
        bool senderIsDead = false;
        if (NetworkManager.Singleton.ConnectedClients.TryGetValue(senderId, out var connected))
        {
            var playerObj = connected.PlayerObject;
            if (playerObj != null)
            {
                var pm = playerObj.GetComponent<PlayerMovement>();
                if (pm != null)
                {
                    senderIsDead = pm.isDead.Value;
                }
            }
        }

        // sender 이름 가져오기
        string senderName = $"Player{senderId}";
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.ConnectedClients.TryGetValue(senderId, out var connectedClient))
        {
            var playerObj = connectedClient.PlayerObject;
            if (playerObj != null)
            {
                var pm = playerObj.GetComponent<PlayerMovement>();
                if (pm != null)
                {
                    string nm = pm.playerName.Value.ToString();
                    if (!string.IsNullOrWhiteSpace(nm))
                        senderName = nm;
                }
            }
        }

        // 닉네임 색 지정 (기존 코드와 동일)
        string coloredName = EscapeRichText(senderName);
        try
        {
            var psMgr = FindFirstObjectByType<PlayerSpectatorManager>();
            int slotNum = 0;
            if (psMgr != null)
            {
                var list = psMgr.playerSlots.Value.PlayerSlots;
                if (list != null)
                {
                    var slot = list.Find(s => s.ClientId == senderId);
                    if (slot.SlotNumber != 0) slotNum = slot.SlotNumber;
                }
            }

            string hex = null;
            switch (slotNum)
            {
                case 1: hex = "#FF0000"; break;
                case 2: hex = "#0000FF"; break;
                case 3: hex = "#90EE90"; break;
                case 4: hex = "#800080"; break;
                case 5: hex = "#FFA500"; break;
                case 6: hex = "#8B4513"; break;
                case 7: hex = "#FFFFFF"; break;
                case 8: hex = "#FFFF00"; break;
                default: hex = null; break;
            }

            if (!string.IsNullOrEmpty(hex))
                coloredName = $"<color={hex}>{EscapeRichText(senderName)}</color>";
        }
        catch
        {
            coloredName = EscapeRichText(senderName);
        }

        string safeMessage = EscapeRichText(message);
        string fullMsg = $"{coloredName}: {safeMessage}";

        // 죽은 플레이어가 보낸 메시지인 경우
        if (senderIsDead)
        {
            Debug.Log($"[PlayerMovement] Dead player {senderId} sent message, only sending to dead players");

            // 죽은 플레이어들만 찾기
            var deadList = new List<ulong>();
            foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
            {
                var clientPlayerObj = client.PlayerObject;
                if (clientPlayerObj != null)
                {
                    var clientPM = clientPlayerObj.GetComponent<PlayerMovement>();
                    if (clientPM != null && clientPM.isDead.Value)
                    {
                        deadList.Add(client.ClientId);
                    }
                }
            }

            if (deadList.Count > 0)
            {
                var deadOnlyParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = deadList.ToArray() } };
                BroadcastMessageClientRpc(fullMsg, deadOnlyParams);
                ShowViewportMessageClientRpc(fullMsg, deadOnlyParams);
            }
        }
        else
        {
            // 살아있는 플레이어가 보낸 메시지 (기존 로직)
            List<ulong> historyTargets;
            if (DayNightManager.Instance != null && !DayNightManager.Instance.isNight.Value)
            {
                historyTargets = NetworkManager.Singleton.ConnectedClientsList.Select(c => c.ClientId).ToList();
            }
            else
            {
                int room = this.RoomId.Value;
                historyTargets = NetworkManager.Singleton.ConnectedClientsList
                    .Where(c =>
                    {
                        var pm = c.PlayerObject.GetComponent<PlayerMovement>();
                        return pm != null && pm.RoomId.Value == room;
                    })
                    .Select(c => c.ClientId)
                    .ToList();
            }
            if (historyTargets.Count == 0) historyTargets.Add(senderId);

            var historyParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = historyTargets.ToArray() } };
            BroadcastMessageClientRpc(fullMsg, historyParams);

            // 죽은 플레이어들에게만 뷰포트 메시지 전송
            var deadList = new List<ulong>();
            foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
            {
                var clientPlayerObj = client.PlayerObject;
                if (clientPlayerObj != null)
                {
                    var clientPM = clientPlayerObj.GetComponent<PlayerMovement>();
                    if (clientPM != null && clientPM.isDead.Value)
                    {
                        deadList.Add(client.ClientId);
                    }
                }
            }

            if (deadList.Count > 0)
            {
                var viewportParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = deadList.ToArray() } };
                ShowViewportMessageClientRpc(fullMsg, viewportParams);
            }
        }
    }

    // 간단한 보안/이스케이프: TextMeshPro rich text 문법 문자(<> 등) 처리
    private string EscapeRichText(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return input.Replace("<", "&lt;").Replace(">", "&gt;");
    }



    [ServerRpc(RequireOwnership = false)]
    public void SetCanMoveTemporaryServerRpc(bool value, float duration, ServerRpcParams rpcParams = default)
    {
        CanMoveVar.Value = value;
        if (value) return; // true면 바로 끝

        // 코루틴 실행
        StartCoroutine(ReenableMovementAfter(duration));
    }

    [ServerRpc(RequireOwnership = false)]
    public void SetPlayerNameServerRpc(string newName, ServerRpcParams rpcParams = default)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        playerName.Value = new FixedString128Bytes(newName);
    }

    [ClientRpc]
    private void BroadcastMessageClientRpc(string fullMessage, ClientRpcParams rpcParams = default)
    {
        if (string.IsNullOrWhiteSpace(fullMessage)) return;

        // 히스토리만 추가하도록 ChatUIController에 분리된 함수 호출
        var ui = ChatUIController.Instance;
        if (ui != null)
        {
            ui.AddHistoryMessage(fullMessage);
            return;
        }

        // fallback: Find
        var fallback = FindFirstObjectByType<ChatUIController>(FindObjectsInactive.Include);
        fallback?.AddHistoryMessage(fullMessage);
    }

    [ClientRpc]
    private void ShowViewportMessageClientRpc(string message, ClientRpcParams rpcParams = default)
    {
        var ui = ChatUIController.Instance;
        if (ui != null)
        {
            ui.ShowViewportMessage(message);
            return;
        }

        var fallback = FindFirstObjectByType<ChatUIController>(FindObjectsInactive.Include);
        fallback?.ShowViewportMessage(message);
    }

    [ClientRpc]
    private void ForceStopWalkingClientRpc(ClientRpcParams rpcParams = default)
    {
        try
        {
            // animator가 있으면 즉시 IsWalking 파라미터를 false로 강제
            if (animator != null)
                animator.SetBool("IsWalking", false);

            // 로컬 소유자라면 네트워크 변수도 즉시 false로 세팅해서 다른 클라이언트의 상태까지 정리
            if (IsOwner)
            {
                // isWalking은 Owner 쓰기 권한이 있을 것이므로 이 클라이언트에서 직접 변경 가능
                isWalking.Value = false;
            }
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"ForceStopWalkingClientRpc failed: {ex}");
        }
    }


    private IEnumerator ReenableMovementAfter(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        CanMoveVar.Value = true;
    }

    // 컨테이너 참조 반환 (HideableObject에서 사용)
    public NetworkObject GetContainerNetworkObject()
    {
        return containerNetObj;
    }

    // 캐릭터 인스턴스 여부 확인
    public bool IsCharacterInstance()
    {
        return isCharacterInstance;
    }

    public void ForceStopWalkingLocal()
    {
        if (!IsOwner) return;

        try
        {
            // 네트워크변수 갱신 (Owner 쓰기 가능)
            isWalking.Value = false;

            // Animator 즉시 정지
            if (animator != null)
                animator.SetBool("IsWalking", false);
        }
        catch (System.Exception ex)
        {
            Debug.LogWarning($"ForceStopWalkingLocal failed: {ex}");
        }
    }
}