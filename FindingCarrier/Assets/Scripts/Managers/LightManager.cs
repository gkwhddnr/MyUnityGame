using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using System.Linq;
using TMPro;
using System;

public class LightManager : NetworkBehaviour
{
    public static LightManager Instance;

    public static event Action OnGameStarted;

    [Header("UI 타이머")]
    public TextMeshProUGUI timerText;

    [Header("테스트용 타이머 길이 (초)")]
    public float dayDuration = 14f;
    public float nightDuration = 5f;

    [SerializeField] private Light directionalLight;
    [SerializeField] private float fadeDuration = 1f;

    [Header("BGM")]
    public AudioSource bgmAudioSource;
    public AudioClip dayBgm;
    public AudioClip nightBgm;

    private Coroutine timerCoroutine;
    private bool gameStarted = false;

    private void Awake()
    {
        if (Instance != null && Instance != this) Destroy(gameObject);
        Instance = this;

        gameStarted = false;
    }

    public void StartGame()
    {
        if (!IsServer) return;
        if (gameStarted) return;

        gameStarted = true;

        // 이벤트 연결
        DayNightManager.Instance.onDayStart.AddListener(StartDayCycle);
        DayNightManager.Instance.onNightStart.AddListener(StartNightCycle);
        DayNightManager.Instance.onGameOver.AddListener(OnGameOver);

        // 모든 클라이언트(호스트 포함)에게 UI 전환 실행하라고 알림
        NotifyClientsGameStartedClientRpc();

        // 낮 시작
        StartDayCycle();

        try
        {
            OnGameStarted?.Invoke();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"OnGameStarted listener threw: {ex}");
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void RequestStartGameServerRpc(ServerRpcParams rpcParams = default)
    {
        if(!IsServer) return;
        // 이 메서드는 서버에서 실행됩니다.
        StartGame();
    }

    public void StartDayCycle()
    {
        UpdatePlayersControl(true);

        if (NetworkManager.Singleton == null) return;
        foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
        {
            var playerObj = client.PlayerObject;
            if(playerObj == null) continue;

            var vision = client.PlayerObject.GetComponent<VisionController>();
            if(vision == null) continue;

            var rpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { client.ClientId } }
            };
            vision?.SetDayVisionClientRpc(rpcParams);
        }

        StopAllPersonalAudiosClientRpc();
        PlayDayBgmClientRpc();
        RestartTimer(dayDuration, isNight: false);
        FadeDirectionalLightClientRpc(new Color(50f / 255f, 50f / 255f, 50f / 255f), fadeDuration);
    }

    public void StartNightCycle()
    {
        UpdatePlayersControl(false);
        if (NetworkManager.Singleton == null) return;
        foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
        {
            var playerObj = client.PlayerObject;
            if (playerObj == null) continue;

            var vision = client.PlayerObject.GetComponent<VisionController>();
            if (vision == null) continue;

            var rpcParams = new ClientRpcParams
            {
                Send = new ClientRpcSendParams { TargetClientIds = new[] { client.ClientId } }
            };
            vision?.SetNightVisionClientRpc(rpcParams);
        }

        PlayNightBgmClientRpc();
        RestartTimer(nightDuration, isNight: true);
        FadeDirectionalLightClientRpc(Color.black, fadeDuration);

    }

    public void OnGameOver()
    {
        Debug.Log("게임 종료: 최대 일수 경과");
        StopTimer();
        // (여기에 게임오버 UI 띄우기 등)
    }

    public void RestartTimer(float duration, bool isNight)
    {
        StopTimer();
        timerCoroutine = StartCoroutine(RunTimer(duration, isNight));
    }

    public void StopTimer()
    {
        if (timerCoroutine != null)
            StopCoroutine(timerCoroutine);
    }

    private IEnumerator RunTimer(float duration, bool isNight)
    {
        float remaining = duration;
        while (remaining > 0f)
        {
            int m = Mathf.FloorToInt(remaining / 60f);
            int s = Mathf.FloorToInt(remaining % 60f);
            bool warning = remaining <= 10f;
            string timeText = $"{m:0}:{s:00}";

            // 全 클라이언트 UI 동기화 (시간, 경고색, 낮/밤 구분)
            UpdateTimerClientRpc(timeText, warning, isNight);

            remaining -= Time.deltaTime;
            yield return null;
        }

        // 사이클 전환
        if(AnyAlivePlayer()) DayNightManager.Instance?.ToggleDayNight();
        else DayNightManager.Instance?.ToggleDayNight();
    }

    [ClientRpc]
    private void StopAllPersonalAudiosClientRpc()
    {
        PlayerMovement[] allPlayerMovements;

#if UNITY_2023_2_OR_NEWER
        // 2023.2+ 권장 API: 비활성 객체 포함, 정렬 없음(더 빠름)
        allPlayerMovements = UnityEngine.Object.FindObjectsByType<PlayerMovement>(
            FindObjectsInactive.Include,
            FindObjectsSortMode.None
        );
#else
    // 이전 버전 fallback: 씬 내/비활성 포함하지만 프리팹(에셋)도 포함될 수 있음 — 주의
    allPlayerMovements = Resources.FindObjectsOfTypeAll<PlayerMovement>();
#endif

        foreach (var pm in allPlayerMovements)
        {
            if (pm == null) continue;
            var src = pm.personalAudioSource;
            if (src != null && src.isPlaying)
            {
                src.Stop();
                Debug.Log($"플레이어 {pm.OwnerClientId} 개인 오디오 중지 (find)");
            }
        }
    }


    [ClientRpc]
    private void UpdateTimerClientRpc(string timeText, bool warning, bool isNight)
    {
        if (timerText == null) return;
        timerText.text = timeText;
        // 남은 시간이 10초 이하면 빨강, 아니면
        // 밤일 때도 빨강 유지, 아침 시작 시 하양으로 돌아감
        if (warning || isNight)
            timerText.color = Color.red;
        else
            timerText.color = Color.white;
    }


    [ClientRpc]
    private void FadeDirectionalLightClientRpc(Color targetColor, float duration)
    {
        StartCoroutine(FadeDirectionalLightRoutine(targetColor, duration));
    }

    [ClientRpc]
    private void NotifyClientsGameStartedClientRpc(ClientRpcParams clientRpcParams = default)
    {
        // UIScreenTransitionManager 싱글톤이 클라이언트에 존재하면 로컬 UI 전환 실행
        UIScreenTransitionManager.Instance?.OnGameStartFromServer();
    }

    private bool AnyAlivePlayer()
    {
        if (NetworkManager.Singleton == null) return false;
        foreach (var c in NetworkManager.Singleton.ConnectedClientsList)
        {
            if (c.PlayerObject != null) return true;
        }
        return false;
    }

    private NetworkObject FindCharacterNetworkObjectByOwner(ulong ownerClientId)
    {
        if (NetworkManager.Singleton == null) return null;
        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var netObj = kv.Value;
            if (netObj == null) continue;
            if (netObj.OwnerClientId != ownerClientId) continue;

            var pm = netObj.GetComponent<PlayerMovement>();
            if (pm != null && pm.IsCharacterInstance())
            {
                return netObj;
            }
        }
        return null;
    }

    // 기존 UpdatePlayersControl 메서드 대신 이 구현을 사용하세요.
    // (LightManager 전체 파일 중 관련 함수들)
    private void UpdatePlayersControl(bool enable)
    {
        if (!IsServer || NetworkManager.Singleton == null) return;

        PlayerCharacterManager pcm = FindFirstObjectByType<PlayerCharacterManager>();

        foreach (var client in NetworkManager.Singleton.ConnectedClientsList)
        {
            ulong clientId = client.ClientId;

            GameObject characterGO = null;
            if (pcm != null)
            {
                characterGO = pcm.GetCharacterByClientId(clientId);
            }

            if (characterGO == null)
            {
                var charNetObj = FindCharacterNetworkObjectByOwner(clientId);
                if (charNetObj != null)
                    characterGO = charNetObj.gameObject;
            }

            var playerObj = client.PlayerObject;
            if (characterGO == null && playerObj != null)
            {
                var pmCheck = playerObj.GetComponent<PlayerMovement>();
                if (pmCheck != null && pmCheck.IsCharacterInstance())
                    characterGO = playerObj.gameObject;
            }

            if (characterGO == null)
            {
                // 캐릭터가 아직 생성 안되어 있으면 재시도 코루틴 실행
                StartCoroutine(EnsureAndSetControlForClient(clientId, enable));
                continue;
            }

            // (이하 기존 로직 — found characterGO 있을 때 처리)
            ApplyControlToCharacter(clientId, characterGO, enable, playerObj);
        }
    }

    // 분리된 재사용 가능한 적용 함수 (characterGO가 확정된 경우)
    private void ApplyControlToCharacter(ulong clientId, GameObject characterGO, bool enable, NetworkObject playerObj)
    {
        var pm = characterGO.GetComponent<PlayerMovement>();
        if (pm == null) return;

        var hideObj = FindHideableForPlayer(clientId);
        bool finalCanMove = (hideObj == null) ? enable : false;

        // LightManager: 서버 컨텍스트에서
        if (!finalCanMove)
        {
            // carrier면 건너뛰기
            if (pm.IsCarrierRoleServer()) pm.ServerSetCanMove(true);
            else pm.ServerSetCanMove(false);
        }

        else pm.ServerSetCanMove(true);
        
        // Vision RPC
        if (playerObj != null)
        {
            var vision = playerObj.GetComponent<VisionController>();
            if (vision != null)
            {
                var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };
                if (finalCanMove)
                {
                    vision.SetDayVisionClientRpc(rpcParams);
                    if (hideObj != null) vision.SetNightVisionClientRpc(rpcParams);
                }
                else
                {
                    vision.SetNightVisionClientRpc(rpcParams);
                }
            }
        }

        // hideObj lighting RPC
        if (hideObj != null)
        {
            var rpcParams2 = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };
            if (enable)
                hideObj.SetDayHideableLightClientRpc(rpcParams2);
            else
                hideObj.SetNightHideableLightClientRpc(rpcParams2);
        }
    }

    // 재시도 코루틴: PCM / SpawnedObjects / PlayerObject 등을 폴링해서 찾아 적용
    private IEnumerator EnsureAndSetControlForClient(ulong clientId, bool enable)
    {
        float start = Time.time;
        float timeout = 3f; // 필요하면 늘리세요
        GameObject found = null;
        PlayerCharacterManager pcm = FindFirstObjectByType<PlayerCharacterManager>();

        while (Time.time - start < timeout)
        {
            // 1) try PCM mapping
            if (pcm != null)
            {
                found = pcm.GetCharacterByClientId(clientId);
                if (found != null) break;
            }

            // 2) SpawnManager scan by OwnerClientId
            if (NetworkManager.Singleton != null)
            {
                foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
                {
                    var netObj = kv.Value;
                    if (netObj == null) continue;
                    if (netObj.OwnerClientId != clientId) continue;

                    var pm = netObj.GetComponent<PlayerMovement>();
                    if (pm != null && pm.IsCharacterInstance())
                    {
                        found = netObj.gameObject;
                        break;
                    }
                }
                if (found != null) break;
            }

            // 3) client.PlayerObject (혹은 다른 경로)
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var client))
            {
                var playerObj = client.PlayerObject;
                if (playerObj != null)
                {
                    var pmcheck = playerObj.GetComponent<PlayerMovement>();
                    if (pmcheck != null && pmcheck.IsCharacterInstance())
                    {
                        found = playerObj.gameObject;
                        break;
                    }
                }
            }

            yield return new WaitForSeconds(0.15f);
        }

        if (found == null)
        {
            Debug.LogWarning($"[LightManager] Could not find character GameObject for client {clientId} after retry.");
            yield break;
        }

        // 이제 실제 적용
        NetworkObject playerObjNet = null;
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.ConnectedClients.TryGetValue(clientId, out var cli))
            playerObjNet = cli.PlayerObject;

        ApplyControlToCharacter(clientId, found, enable, playerObjNet);
    }

    // 플레이어 소유 클라이언트 ID로 숨는 오브젝트 찾기 (서버 전용)
    private HideableObject FindHideableForPlayer(ulong clientId)
    {
        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var netObj = kv.Value; // NetworkObject
            var hideObj = netObj.GetComponent<HideableObject>();
            if (hideObj != null && hideObj.CurrentPlayerOwnerId == clientId)
            {
                return hideObj;
            }
        }
        return null;
    }

    private IEnumerator FadeDirectionalLightRoutine(Color targetColor, float duration)
    {
        if (directionalLight == null) yield break;

        Color initial = directionalLight.color;
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            directionalLight.color = Color.Lerp(initial, targetColor, t / duration);
            yield return null;
        }
        directionalLight.color = targetColor;
    }

    [ClientRpc]
    private void PlayDayBgmClientRpc()
    {
        if (bgmAudioSource == null || dayBgm == null) return;

        // 기존에 재생 중인 BGM이 있다면 정지
        if (bgmAudioSource.isPlaying)
        {
            bgmAudioSource.Stop();
        }

        bgmAudioSource.clip = dayBgm;
        bgmAudioSource.loop = true; // BGM이므로 반복 재생
        bgmAudioSource.Play();
    }

    [ClientRpc]
    private void PlayNightBgmClientRpc()
    {
        if (bgmAudioSource == null || nightBgm == null) return;

        // 기존에 재생 중인 BGM이 있다면 정지
        if (bgmAudioSource.isPlaying)
        {
            bgmAudioSource.Stop();
        }

        bgmAudioSource.clip = nightBgm;
        bgmAudioSource.loop = true; // BGM이므로 반복 재생
        bgmAudioSource.Play();
    }
}
