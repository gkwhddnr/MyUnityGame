using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.Netcode;
using UnityEngine;

public class EmergencyBell : NetworkBehaviour
{
    [Header("설정")]
    [SerializeField] private Collider bellCollider; // Plane의 Collider
    [SerializeField] private Transform teleportPosition; // 텔레포트 위치

    [Header("UI")]
    [SerializeField] private TMP_Text statusText; // 상태 텍스트 (인스펙터 연결)

    [Header("사운드")]
    [SerializeField] private AudioSource audioSource;


    private NetworkList<ulong> playersOnBell; // 현재 Bell 위에 있는 플레이어 OwnerClientId 목록 (생존자만)
    private HashSet<ulong> playersInBell = new HashSet<ulong>();
    private int currentDay = 1;
    private float enableDelayRemaining = 0f;

    private bool triggerActivated = false;
    private bool twoPlayerMode = false;

    private Coroutine enableCoroutine;

    public enum BellState : byte
    {
        Disabled = 0, // 밤이거나 비활성(작동불가)
        Waiting = 1,  // 낮이 되었지만 5초 대기중 (초 카운트 중)
        Ready = 2,    // 활성(초록)
        Activated = 3 // 발동된 상태 (빨강)
    }

    // 핵심 네트워크 변수(단일 진실)
    private NetworkVariable<BellState> netBellState = new NetworkVariable<BellState>(
        BellState.Disabled, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private NetworkVariable<int> netAliveCount = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private void Awake()
    {
        playersOnBell = new NetworkList<ulong>();
        if (bellCollider == null) bellCollider = GetComponent<Collider>();

        if (bellCollider != null) bellCollider.isTrigger = true;
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        // 클라이언트에서 UI 갱신: 단일 진실 구독
        netBellState.OnValueChanged += (oldV, newV) => UpdateBellTextColor();
        netAliveCount.OnValueChanged += (oldV, newV) => UpdateBellTextColor();

        // DayNightManager.isNight 변화는 서버에서 상태를 바꾸도록 처리 (클라이언트는 netBellState 만 사용)
        if (DayNightManager.Instance != null && IsServer)
        {
            DayNightManager.Instance.onDayStart.AddListener(() =>
            {
                currentDay = DayNightManager.Instance.currentDay;
                SetBellActive(true);
            });

            DayNightManager.Instance.onNightStart.AddListener(() =>
            {
                currentDay = DayNightManager.Instance.currentDay;
                SetBellActive(false);
            });
        }

        // 서버 초기화
        if (IsServer)
        {
            currentDay = DayNightManager.Instance != null ? DayNightManager.Instance.currentDay : 1;
            triggerActivated = false;
            twoPlayerMode = false;

            // 갱신해 줌
            UpdateAliveCountServer();
            // 초기 상태: 낮이면 활성화 체크, 아니면 비활성
            if (DayNightManager.Instance != null && DayNightManager.Instance.isNight.Value)
                SetBellStateServer(BellState.Disabled);
            else
                SetBellStateServer(BellState.Disabled); // 기본 Disabled, 이후 SetBellActive(true)에서 Waiting/Ready로 바뀜
        }

        // 초기 UI 갱신 (클라이언트)
        UpdateBellTextColor();
    }

    // 서버 전용: Bell 활성화/비활성 처리 (Day/Night 전환 시)
    private void SetBellActive(bool active)
    {
        if (!IsServer) return;

        triggerActivated = false;
        twoPlayerMode = false;

        playersInBell.Clear();
        playersOnBell.Clear();

        // 항상 최신 alive count 저장
        UpdateAliveCountServer();

        // 2명 이하이면 항상 비활성화 모드
        if (active && netAliveCount.Value <= 2)
        {
            twoPlayerMode = true;
            if (bellCollider != null) bellCollider.enabled = true;

            // 상태는 Disabled (작동불가)
            SetBellStateServer(BellState.Disabled);
            return;
        }

        if (bellCollider != null) bellCollider.enabled = active;

        // 중복 코루틴 방지
        if (enableCoroutine != null)
        {
            StopCoroutine(enableCoroutine);
            enableCoroutine = null;
        }

        if (active)
        {
            // 이미 씬에 올라와 있는 플레이어들 채우기
            PopulateCurrentPlayersInCollider();

            // 낮이면 Waiting 상태로 두고 5초 뒤 Ready로 전환
            SetBellStateServer(BellState.Waiting);
            enableCoroutine = StartCoroutine(EnableTriggerAfterDelay(5f));
        }
        else
        {
            // 비활성 (예: 밤)
            SetBellStateServer(BellState.Disabled);
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!IsServer) return;
        if (triggerActivated) return;
        if (bellCollider == null || !bellCollider.enabled) return;
        if (other.gameObject.layer != LayerMask.NameToLayer("Player")) return;

        var playerNetObj = other.GetComponentInParent<NetworkObject>() ?? other.GetComponent<NetworkObject>();
        if (playerNetObj == null) return;

        ulong clientId = playerNetObj.OwnerClientId;

        UpdateAliveCountServer(); // 최신화

        if (twoPlayerMode)
        {
            var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };
            SendBellMessageClientRpc("현재 살아있는 플레이어가 <color=red>2명입니다! (호출불가)</color>", rpcParams);
            return;
        }

        // 1일차 처리
        if (currentDay == 1)
        {
            var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };
            SendBellMessageClientRpc("<color=green>다음 날</color>부터 사용할 수 있습니다.", rpcParams);
            return;
        }

        // 이미 들어온 경우는 기존 동작 유지
        if (playersInBell.Contains(clientId))
        {
            var healthUpdate = playerNetObj.GetComponent<PlayerHealth>();
            if (healthUpdate != null && healthUpdate.Health.Value > 0f && !playersOnBell.Contains(clientId))
                playersOnBell.Add(clientId);

            var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };
            SendBellMessageClientRpc(
                "살아있는 플레이어들 중 <color=yellow>절반</color>이 모이면 모든 플레이어를 가운데로 호출할 수 있습니다.\n<color=red>(2명 이하로 남을 시 작동불가)</color>",
                rpcParams);
            return;
        }

        playersInBell.Add(clientId);

        var health = playerNetObj.GetComponent<PlayerHealth>();
        if (health != null && health.Health.Value > 0f)
        {
            if (!playersOnBell.Contains(clientId))
                playersOnBell.Add(clientId);
        }

        // 현재 Waiting 상태이면, Waiting의 남은 초를 방금 들어온 사람에게 즉시 전송
        if (netBellState.Value == BellState.Waiting)
        {
            int secondsLeft = Mathf.CeilToInt(enableDelayRemaining);
            // 타겟을 단일 플레이어로 보내기 위해 배열 생성
            var singleTarget = new ulong[] { clientId };
            var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = singleTarget } };
            SendBellCountdownClientRpc(secondsLeft, rpcParams);
        }

        // 현재 상태가 Ready 일때만 TryActivate 시도 (Waiting이면 아직 준비중)
        if (netBellState.Value == BellState.Ready)
        {
            TryActivateTrigger();
        }
        else
        {
            // 준비중 안내
            var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };
            SendBellMessageClientRpc("날이 밝은 후 5초 후에 활성화됩니다.", rpcParams);
        }

        // 서버는 alive count 최신화
        UpdateAliveCountServer();
    }

    private void OnTriggerExit(Collider other)
    {
        if (!IsServer || triggerActivated) return;
        if (other.gameObject.layer != LayerMask.NameToLayer("Player")) return;

        var playerNetObj = other.GetComponentInParent<NetworkObject>() ?? other.GetComponent<NetworkObject>();
        if (playerNetObj == null) return;

        ulong clientId = playerNetObj.OwnerClientId;
        playersOnBell.Remove(clientId);
        playersInBell.Remove(clientId);

        UpdateAliveCountServer();
    }

    private void PopulateCurrentPlayersInCollider()
    {
        if (bellCollider == null) return;
        // LayerMask for Player
        int playerLayer = LayerMask.NameToLayer("Player");
        if (playerLayer < 0) playerLayer = 0;
        int layerMask = 1 << playerLayer;

        // Collider bounds 기반 OverlapBox 사용
        var bounds = bellCollider.bounds;
        Vector3 center = bounds.center;
        Vector3 halfExtents = bounds.extents;
        Quaternion orientation = bellCollider.transform.rotation;

        Collider[] overlaps = Physics.OverlapBox(center, halfExtents, orientation, layerMask);
        foreach (var c in overlaps)
        {
            if (c == null) continue;
            var netObj = c.GetComponentInParent<NetworkObject>() ?? c.GetComponent<NetworkObject>();
            if (netObj == null) continue;
            ulong clientId = netObj.OwnerClientId;
            playersInBell.Add(clientId);

            // 실제 캐릭터에서 생존 체크
            var charNetObj = GetCharacterNetworkObjectForClient(clientId);
            if (charNetObj != null)
            {
                var health = charNetObj.GetComponent<PlayerHealth>();
                if (health != null && health.Health.Value > 0f)
                {
                    if (!playersOnBell.Contains(clientId))
                        playersOnBell.Add(clientId);
                }
            }
        }
    }

    private void TryActivateTrigger()
    {
        if (!IsServer) return;
        if (triggerActivated) return;

        UpdateAliveCountServer(); // 최신 인원 수

        int aliveCount = netAliveCount.Value;

        if (aliveCount <= 2)
        {
            // 2명 이하이면 발동 불가
            SetBellStateServer(BellState.Disabled);
            return;
        }

        int requiredCount = (aliveCount % 2 == 0) ? (aliveCount / 2) : ((aliveCount + 1) / 2);

        if (playersOnBell.Count >= requiredCount)
        {
            triggerActivated = true;
            SetBellStateServer(BellState.Activated);

            if (audioSource != null)
            {
                audioSource.volume = 1f;
                audioSource.Play();

                // 페이드 아웃 코루틴 시작
                StartCoroutine(FadeOutVolume(audioSource, 1.5f)); // 1.5초 동안 서서히 꺼지게
            }

            // 발동: teleport (ServerRpc)
            TeleportAllAlivePlayersServerRpc();

            // global message
            string message = "현재 살아있는 플레이어들 중 <color=yellow>절반</color>이 로비에 모여 호출되었습니다.";
            GlobalSendBellMessageClientRpc(message);
        }
    }

    private IEnumerator FadeOutVolume(AudioSource source, float duration)
    {
        if (source.clip == null) yield break;

        float startVolume = source.volume;

        // clip 재생 후 마지막 duration 만큼만 서서히 줄이기
        yield return new WaitForSeconds(source.clip.length - duration);

        float time = 0f;
        while (time < duration)
        {
            time += Time.deltaTime;
            source.volume = Mathf.Lerp(startVolume, 0f, time / duration);
            yield return null;
        }

        source.Stop();
        source.volume = startVolume;
    }


    private IEnumerator EnableTriggerAfterDelay(float delay)
    {
        // 서버에서만 실행
        if (!IsServer) yield break;

        float elapsed = 0f;
        enableDelayRemaining = delay;

        // 대기 상태로 설정 (Waiting)
        SetBellStateServer(BellState.Waiting);

        // 1초 단위로 남은 시간 전송 (playersOnBell 대상)
        while (elapsed < delay)
        {
            int secondsLeft = Mathf.CeilToInt(delay - elapsed);
            enableDelayRemaining = delay - elapsed;

            // playersOnBell에 있는 플레이어들에게만 전송 (NetworkList -> ulong[] 변환 안전 처리)
            if (playersOnBell != null && playersOnBell.Count > 0)
            {
                ulong[] targetIds = new ulong[playersOnBell.Count];
                for (int i = 0; i < playersOnBell.Count; i++)
                {
                    targetIds[i] = playersOnBell[i];
                }

                var clientParams = new ClientRpcParams
                {
                    Send = new ClientRpcSendParams { TargetClientIds = targetIds }
                };

                SendBellCountdownClientRpc(secondsLeft, clientParams);
            }

            // 1초 단위로 카운트 (부드럽게 하고 싶으면 더 세분화)
            float waitStep = 1f;
            float stepElapsed = 0f;
            while (stepElapsed < waitStep && elapsed < delay)
            {
                stepElapsed += Time.deltaTime;
                elapsed += Time.deltaTime;
                yield return null;
            }
        }

        enableDelayRemaining = 0f;
        UpdateAliveCountServer();

        if (netAliveCount.Value <= 2)
        {
            SetBellStateServer(BellState.Disabled);
        }
        else
        {
            SetBellStateServer(BellState.Ready);

            // 모든 클라이언트에게 활성화 알림 (글로벌)
            GlobalSendBellMessageClientRpc("비상벨이 활성화됐습니다.");
        }

        yield break;
    }

    private int GetAlivePlayerCount()
    {
        if (NetworkManager.Singleton == null) return 0;

        int cnt = 0;
        foreach (var c in NetworkManager.Singleton.ConnectedClientsList)
        {
            // 실제 캐릭터 기준으로 PlayerHealth 확인
            var charNetObj = GetCharacterNetworkObjectForClient(c.ClientId);
            if (charNetObj == null) continue;
            var health = charNetObj.GetComponent<PlayerHealth>();
            if (health != null && health.Health.Value > 0f) cnt++;
        }
        return cnt;
    }

    // 서버 전용: netAliveCount 갱신
    private void UpdateAliveCountServer()
    {
        if (!IsServer) return;
        netAliveCount.Value = GetAlivePlayerCount();
    }

    // 실제 캐릭터 NetworkObject 반환 (PlayerCharacterManager 우선, 폴백으로 SpawnedObjects OwnerClientId 검색)
    private NetworkObject GetCharacterNetworkObjectForClient(ulong clientId)
    {
        // 1) PlayerCharacterManager 사용
        var pcm = FindFirstObjectByType<PlayerCharacterManager>();
        if (pcm != null)
        {
            var go = pcm.GetCharacterByClientId(clientId);
            if (go != null)
            {
                var no = go.GetComponent<NetworkObject>();
                if (no != null) return no;
            }
        }

        // 2) 폴백: SpawnedObjects 탐색해서 PlayerHealth 붙은 오브젝트(또는 PlayerMovement.IsCharacterInstance()) 찾기
        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var netObj = kv.Value;
            if (netObj == null) continue;
            if (netObj.OwnerClientId != clientId) continue;

            // 우선 PlayerHealth가 붙어있으면 이것이 실제 캐릭터일 가능성 높음
            if (netObj.GetComponent<PlayerHealth>() != null) return netObj;

            // 또는 PlayerMovement가 있고 IsCharacterInstance이면 캐릭터
            var pm = netObj.GetComponent<PlayerMovement>();
            if (pm != null && pm.IsCharacterInstance()) return netObj;
        }

        return null;
    }

    [ServerRpc(RequireOwnership = false)]
    private void TeleportAllAlivePlayersServerRpc()
    {
        // 모든 살아있는 플레이어(실제 캐릭터 기준)를 수집
        var aliveList = new List<(NetworkObject characterNetObj, ulong clientId)>();
        foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
        {
            var charNetObj = GetCharacterNetworkObjectForClient(client.ClientId);
            if (charNetObj == null) continue;
            var health = charNetObj.GetComponent<PlayerHealth>();
            if (health == null || health.Health.Value <= 0f) continue;

            aliveList.Add((charNetObj, client.ClientId));
        }

        if (aliveList.Count == 0) return;

        // 8방향 오프셋 (XZ 평면 기준, 시계 방향)
        Vector3[] offsets = new Vector3[]
        {
        new Vector3(-1, 0, 1),   // 좌상
        new Vector3(-1, 0, 0),   // 좌
        new Vector3(-1, 0, -1),  // 좌하
        new Vector3(0, 0, -1),   // 하
        new Vector3(1, 0, -1),   // 우하
        new Vector3(1, 0, 0),    // 우
        new Vector3(1, 0, 1),    // 우상
        new Vector3(0, 0, 1)     // 상
        };

        float offsetDistance = 2f; // 플레이어 간 간격
        Vector3 centerPos = teleportPosition.position;

        for (int i = 0; i < aliveList.Count; i++)
        {
            var entry = aliveList[i];
            Vector3 dir = offsets[i % offsets.Length].normalized;
            Vector3 targetPos = centerPos + dir * offsetDistance;
            Quaternion targetRot = teleportPosition.rotation;

            // 숨은 플레이어 강제 언하이드: HideableObject 딕셔너리에서 찾고 처리
            if (HideableObject.HideableByOwner.TryGetValue(entry.clientId, out var hideable))
            {
                hideable.ForceUnhideServer(entry.characterNetObj);
                hideable.SetUnhideClientRpc(entry.characterNetObj.NetworkObjectId);
                hideable.ApplyVisionAndLightUnhideClientRpc(entry.clientId);
                hideable.TogglePlayerRendererClientRpc(entry.clientId, false);
            }

            MovePlayerClientRpc(entry.characterNetObj.NetworkObjectId, targetPos, targetRot);
        }

        // Bell 트리거 비활성화 + 상태 리셋 후 alive count 갱신
        if (bellCollider != null) bellCollider.enabled = false;
        triggerActivated = false;
        twoPlayerMode = false;

        // 코루틴 정리
        if (enableCoroutine != null)
        {
            StopCoroutine(enableCoroutine);
            enableCoroutine = null;
        }

        // 업데이트된 인원 반영
        UpdateAliveCountServer();

        // 발동 이후에는 Disabled로 전환
        SetBellStateServer(BellState.Disabled);
    }


    [ClientRpc]
    private void SendBellMessageClientRpc(string message, ClientRpcParams rpcParams = default)
    {
        PersonalNotificationManager.Instance?.ShowPersonalMessage(message);
    }

    [ClientRpc]
    private void GlobalSendBellMessageClientRpc(string message)
    {
        var g = FindFirstObjectByType<GlobalNotificationManager>();
        g?.ShowGlobalMessageClientRpc(message);
    }

    [ClientRpc]
    private void SendBellCountdownClientRpc(int secondsLeft, ClientRpcParams rpcParams = default)
    {
        if (PersonalNotificationManager.Instance != null)
        {
            if (secondsLeft > 1)
                PersonalNotificationManager.Instance.ShowPersonalMessage($"<color=green>{secondsLeft}</color>초 후 비상벨이 활성화됩니다.");

            else if (secondsLeft == 1)
                PersonalNotificationManager.Instance.ShowPersonalMessage($"<color=red>{secondsLeft}</color>초 후 비상벨이 활성화됩니다.");

            else
                PersonalNotificationManager.Instance.ShowPersonalMessage("비상벨을 활성화합니다!");
            
            return;
        }

        // fallback: inactive 포함해서 찾아서 표시
        var personalUI = FindFirstObjectByType<PersonalNotificationManager>(FindObjectsInactive.Include);
        if (personalUI != null)
        {
            if (secondsLeft > 1)
                personalUI.ShowPersonalMessage($"<color=green>{secondsLeft}</color>초 후 비상벨이 활성화됩니다.");
            else if(secondsLeft == 1) personalUI.ShowPersonalMessage($"<color=red>{secondsLeft}</color>초 후 비상벨이 활성화됩니다.");
            else personalUI.ShowPersonalMessage("비상벨을 활성화합니다!");
        }
    }

    [ClientRpc]
    private void MovePlayerClientRpc(ulong playerNetId, Vector3 targetPos, Quaternion targetRot)
    {
        if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(playerNetId, out var playerNetObj)) return;
        var player = playerNetObj.gameObject;

        // Rigidbody 복구
        var rb = player.GetComponent<Rigidbody>();
        if (rb != null) rb.isKinematic = false;

        player.transform.SetPositionAndRotation(targetPos, targetRot);

        // 숨은 플레이어 복구 보조: 로컬에서 렌더러 / 라이트 강제 복구
        if (NetworkManager.Singleton.LocalClientId == playerNetObj.OwnerClientId)
        {
            var rend = player.GetComponentInChildren<Renderer>();
            if (rend != null) rend.enabled = true;

            var pLight = player.GetComponentInChildren<Light>();
            if (pLight != null) pLight.enabled = true;
        }

        // 은신 상태라면 해제 시도 (이전 코드 재사용)
        var hideObj = player.GetComponentInChildren<HideableObject>();
        if (hideObj != null)
        {
            try
            {
                hideObj.SetUnhideClientRpc(playerNetObj.NetworkObjectId);
            }
            catch
            {
                var rend = player.GetComponentInChildren<Renderer>();
                if (rend != null) rend.enabled = true;
                var pLight = player.GetComponentInChildren<Light>();
                if (pLight != null) pLight.enabled = true;
            }
        }
    }

    // 서버 전용: Bell 상태를 변경하고 netAliveCount도 갱신
    private void SetBellStateServer(BellState newState)
    {
        if (!IsServer) return;
        netBellState.Value = newState;
        UpdateAliveCountServer();
    }

    // 클라이언트에서 텍스트 색 변경 (오직 netBellState + netAliveCount 만 사용)
    private void UpdateBellTextColor()
    {
        if (statusText == null) return;

        BellState state = netBellState.Value;
        int alive = netAliveCount.Value;

        // 규칙:
        // - Ready (낮 + 활성화) && alive >= 3 -> 초록
        // - 그 외(Disabled, Waiting, Activated, alive<=2) -> 빨강
        if (state == BellState.Ready && alive >= 3)
        {
            statusText.color = Color.green;
        }
        else
        {
            statusText.color = Color.red;
        }
    }
}
