using UnityEngine;
using UnityEngine.UI;
using System.Collections;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using TMPro;
using Unity.Collections;
using UnityEditor;
using Unity.Multiplayer.Widgets;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Multiplayer;
using System;



#if UNITY_NETCODE || ENABLE_MONO || UNITY_2021_1_OR_NEWER
using Unity.Netcode;
#endif

public class UIScreenTransitionManager : NetworkBehaviour
{
    public static UIScreenTransitionManager Instance;

    [Header("Optional Input/UI (Assign if used)")]
    public InputField idInputField;
    public TMP_InputField inputField;
    public TMP_InputField inputField2;

    [Header("Transition Sets")]
    public Button[] transitionButtons;
    public CanvasGroup[] currentCanvasGroups;
    public CanvasGroup[] nextCanvasGroups;

    [Header("Fade Panel (full screen black)")]
    public CanvasGroup fadeCanvasGroup;

    [Header("Transition Settings")]
    public float fadeDuration = 1.0f;

    [Header("Game Start")]
    public Button gameStartButton; // 대기실의 게임 스타트 버튼
    private List<GameObject> activePlayers = new List<GameObject>();

    [Header("Session Player List (assign Content transform if possible)")]
    public Transform sessionPlayerListContent; // 인스펙터에 Content(ScrollRect.content)를 연결

    [Header("Password Display")]
    public TogglePasswordUI togglePasswordUI;

    [Tooltip("세션 목록에 비밀번호를 표시할 Text 컴포넌트입니다.")]
    public TMP_InputField passwordDisplay;

    [Header("Custom Exit Button (for kicked players)")]
    public Button exitButton; // Inspector에 할당
    private CanvasGroup exitButtonCanvasGroup;

    private readonly NetworkVariable<FixedString128Bytes> syncPassword = new NetworkVariable<FixedString128Bytes>(
        "",
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    [SerializeField] private SessionNameHelper sessionNameHelper;
    private bool isTransitioning = false;
    private bool allowTransition = false;
    private int initialScreenIndex = 0;

    public void Awake()
    {
        // 버튼 항상 화면에 표시
        if (gameStartButton != null)
        {
            gameStartButton.gameObject.SetActive(true);
            gameStartButton.interactable = false;
            gameStartButton.onClick.AddListener(OnCreateSessionClicked);
        }
        Instance = this;
    }

    void Start()
    {
        // 화면 초기화
        for (int i = 0; i < currentCanvasGroups.Length; i++)
            InitCanvasGroup(currentCanvasGroups[i], false);
        for (int i = 0; i < nextCanvasGroups.Length; i++)
            InitCanvasGroup(nextCanvasGroups[i], false);

        if (initialScreenIndex >= 0 && initialScreenIndex < currentCanvasGroups.Length)
            InitCanvasGroup(currentCanvasGroups[initialScreenIndex], true);

        // fade 초기화
        if (fadeCanvasGroup != null)
        {
            fadeCanvasGroup.gameObject.SetActive(false);
            fadeCanvasGroup.alpha = 0f;
            fadeCanvasGroup.interactable = false;
            fadeCanvasGroup.blocksRaycasts = false;

            EnsureFullScreenRectTransform(fadeCanvasGroup);
            EnsureFadePanelOnTop();
        }

        // Input 필드 이벤트
        if (inputField != null) inputField.onValueChanged.AddListener(OnInputValueChanged);
        if (inputField2 != null) inputField2.onValueChanged.AddListener(OnInputValueChanged);
        if (idInputField != null) idInputField.onValueChanged.AddListener(OnInputValueChanged);
        UpdateLoginButtonInteractable();

        // 전환 버튼 이벤트
        for (int i = 0; i < transitionButtons.Length; i++)
        {
            int idx = i;
            if (transitionButtons[idx] != null)
                transitionButtons[idx].onClick.AddListener(() => OnTransitionButtonClicked(idx));
        }

        if (exitButton != null)
        {
            exitButtonCanvasGroup = exitButton.GetComponent<CanvasGroup>();
            if (exitButtonCanvasGroup == null)
                exitButtonCanvasGroup = exitButton.gameObject.AddComponent<CanvasGroup>();

            // 처음엔 투명·비활성
            exitButtonCanvasGroup.alpha = 0f;
            exitButtonCanvasGroup.interactable = false;
            exitButtonCanvasGroup.blocksRaycasts = false;
            exitButton.onClick.AddListener(OnExitButtonClicked);
        }

        if (gameStartButton != null)
        {
            if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsHost)
            {
                gameStartButton.gameObject.SetActive(true); // 호스트만 보임
                gameStartButton.interactable = false;
            }
            else
            {
                gameStartButton.gameObject.SetActive(false); // 클라이언트는 숨김
            }
        }

        // 게임 스타트 버튼 이벤트
        if (gameStartButton != null)
            gameStartButton.onClick.AddListener(OnGameStartClicked);
    }

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();
        syncPassword.OnValueChanged += (_, newVal) => {
            if (passwordDisplay != null)
                passwordDisplay.text = newVal.ToString();
        };

        if (IsServer)
        {
            NetworkManager.Singleton.OnClientConnectedCallback += SendPasswordToNewClient;
            // 클라이언트 입장/퇴장 이벤트 구독
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnected;

            // 호스트(자기 자신)도 activePlayers에 미리 등록
            RegisterPlayer(NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject().gameObject);
        }

        // NetworkVariable 동기화 받기
        syncPassword.OnValueChanged += (_, newVal) =>
        {
            if (passwordDisplay != null)
                passwordDisplay.text = newVal.ToString();
        };
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer)
        {
            NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnected;
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnected;
        }
        base.OnNetworkDespawn();
    }

    private void SendPasswordToNewClient(ulong clientId) { }

    void Update()
    {
        if (gameStartButton == null) return;

        // 활성 플레이어만 카운트
        int count = 0;
        foreach (var player in activePlayers)
        {
            if (player != null && player.activeInHierarchy)
                count++;
        }

        gameStartButton.interactable = (count >= 1); // 일정 이상의 플레이어가 모이면 "게임시작"버튼 활성화
    }

    public void RegisterPlayer(GameObject player)
    {
        if (!activePlayers.Contains(player))
            activePlayers.Add(player);

        // 서버 & 네트워크 작동 중 & 매니저 있을 때만 갱신
    }

    public void UnregisterPlayer(GameObject player)
    {
        if (activePlayers.Contains(player))
            activePlayers.Remove(player);
    }

    public void OnGameStartClicked()
    {
        // 중복 진입 방지
        if (isTransitioning) return;

        if (LightManager.Instance != null && gameStartButton.interactable == true)
        {
            if (LightManager.Instance.IsServer) // 호스트
            {
                LightManager.Instance.StartGame();
            }
            else // 클라이언트
            {
                LightManager.Instance.RequestStartGameServerRpc();
            }
        }

        // 코루틴으로 전체 시퀀스 처리
        StartCoroutine(GameStartSequence());
    }

    private void InitCanvasGroup(CanvasGroup cg, bool active)
    {
        if (cg == null) return;
        cg.gameObject.SetActive(active);
        cg.alpha = active ? 1f : 0f;
        cg.interactable = active;
        cg.blocksRaycasts = active;
    }

    private void OnInputValueChanged(string newValue) => UpdateLoginButtonInteractable();
    private void UpdateLoginButtonInteractable()
    {
        if (transitionButtons == null || transitionButtons.Length == 0) return;
        bool tmpHas = inputField != null && !string.IsNullOrEmpty(inputField.text?.Trim());
        bool tmpHas2 = inputField2 != null && !string.IsNullOrEmpty(inputField2.text?.Trim());
        bool legacyHas = idInputField != null && !string.IsNullOrEmpty(idInputField.text?.Trim());
        bool active = (tmpHas || legacyHas || tmpHas2);
        if (transitionButtons[0] != null) transitionButtons[0].interactable = active;
    }


    public void OnGameStartFromServer()
    {
        if (isTransitioning) return;
        StartCoroutine(GameStartSequence());
    }

    public void OnTransitionButtonClicked(int index)
    {
        if (isTransitioning) return;
        if (index < 0 || index >= currentCanvasGroups.Length || index >= nextCanvasGroups.Length) return;
        if (fadeCanvasGroup == null) return;

        // 현재 이 MonoBehaviour가 활성화되어 있으면 자기자신에서 코루틴 시작,
        // 아니라면 안전한 글로벌 Runner에서 시작
        var routine = TransitionRoutine(currentCanvasGroups[index], nextCanvasGroups[index]);

        if (isActiveAndEnabled) // MonoBehaviour 활성+활동 여부 체크
        {
            StartCoroutine(routine);
        }
        else
        {
            // 안전한 전역 Runner에서 실행
            CoroutineRunner.Instance.Run(routine);
        }
    }

    public void EnableTransition() => allowTransition = true;

    private IEnumerator GameStartSequence()
    {
        isTransitioning = true;

        // -- 임시로 fadeDuration을 2초로 설정하고, 끝나면 복구 --
        float oldFadeDuration = fadeDuration;
        fadeDuration = 2f;

        try
        {
            // 1) 즉시 입력 차단 (인터랙션만 끄고 GameObjects는 나중에 끌 예정)
            if (transitionButtons != null)
            {
                foreach (var b in transitionButtons)
                    if (b != null) b.interactable = false;
            }
            if (inputField != null) inputField.interactable = false;
            if (inputField2 != null) inputField2.interactable = false;
            if (idInputField != null) idInputField.interactable = false;
            if (gameStartButton != null) gameStartButton.interactable = false;

            // 2) 페이드아웃만 수행 (from: current 화면의 initialScreenIndex)
            CanvasGroup from = null;
            CanvasGroup to = null;

            if (currentCanvasGroups != null && initialScreenIndex >= 0 && initialScreenIndex < currentCanvasGroups.Length)
                from = currentCanvasGroups[initialScreenIndex];

            if (nextCanvasGroups != null && nextCanvasGroups.Length > 0)
                to = nextCanvasGroups[0];

            // 준비: fade 패널 활성화
            if (fadeCanvasGroup != null)
            {
                EnsureFadePanelOnTop();
                fadeCanvasGroup.gameObject.SetActive(true);
                fadeCanvasGroup.interactable = true;
                fadeCanvasGroup.blocksRaycasts = true;
                // 보장: 시작 알파 범위
                fadeCanvasGroup.alpha = Mathf.Clamp01(fadeCanvasGroup.alpha);
            }

            float startFrom = (from != null) ? from.alpha : 0f;
            float startFade = (fadeCanvasGroup != null) ? fadeCanvasGroup.alpha : 0f;

            float t = 0f;
            while (t < fadeDuration)
            {
                t += Time.deltaTime;
                float norm = Mathf.Clamp01(t / fadeDuration);

                if (from != null)
                    from.alpha = Mathf.Lerp(startFrom, 0f, norm);   // 화면 1 -> 0

                if (fadeCanvasGroup != null)
                    fadeCanvasGroup.alpha = Mathf.Lerp(startFade, 1f, norm); // 검정 0 -> 1

                yield return null;
            }

            // 정확한 최종값 보장
            if (from != null) from.alpha = 0f;
            if (fadeCanvasGroup != null) fadeCanvasGroup.alpha = 1f;

            // 3) **페이드아웃이 끝난 시점에만** 서버에게 게임 시작을 요청해서 타이머가 이제부터 흐르도록 함.
            if (LightManager.Instance != null)
            {
                // 호스트(서버)라면 직접 호출, 클라이언트라면 ServerRpc로 요청
                if (LightManager.Instance.IsServer)
                {
                    LightManager.Instance.StartGame();
                }
                else
                {
                    // 요청을 서버로 보냄 (ServerRpc)
                    LightManager.Instance.RequestStartGameServerRpc();
                }
            }

            // 4) 검정으로 완전히 덮인 상태에서 즉시 게임 UI 노출 (페이드 인은 하지 않음)
            if (from != null)
                from.gameObject.SetActive(false);

            if (to != null)
            {
                SafeSetActive(to.gameObject, true);
                to.alpha = 1f;
                to.interactable = true;
                to.blocksRaycasts = true;
            }

            // 페이드 패널을 즉시 비활성화하여 게임 UI가 보이도록 함 (페이드 인 루틴 없음)
            if (fadeCanvasGroup != null)
            {
                fadeCanvasGroup.interactable = false;
                fadeCanvasGroup.blocksRaycasts = false;
                fadeCanvasGroup.gameObject.SetActive(false);
            }

            // 5) 로비 관련 UI 완전 제거/숨김
            if (transitionButtons != null)
            {
                foreach (var b in transitionButtons)
                {
                    if (b == null) continue;
                    b.gameObject.SetActive(false);
                }
            }

            if (currentCanvasGroups != null)
            {
                foreach (var cg in currentCanvasGroups)
                {
                    if (cg == null) continue;
                    cg.gameObject.SetActive(false);
                }
            }

            if (nextCanvasGroups != null)
            {
                for (int i = 0; i < nextCanvasGroups.Length; i++)
                {
                    var cg = nextCanvasGroups[i];
                    if (cg == null) continue;
                    if (cg != to) cg.gameObject.SetActive(false);
                }
            }

            if (gameStartButton != null) gameStartButton.gameObject.SetActive(false);
            if (inputField != null) inputField.gameObject.SetActive(false);
            if (inputField2 != null) inputField2.gameObject.SetActive(false);
            if (idInputField != null) idInputField.gameObject.SetActive(false);
        }
        finally
        {
            // 반드시 복구
            fadeDuration = oldFadeDuration;
            isTransitioning = false;
        }

        yield break;
    }

    private IEnumerator TransitionRoutine(CanvasGroup from, CanvasGroup to)
    {
        isTransitioning = true;
        EnsureFadePanelOnTop();
        fadeCanvasGroup.gameObject.SetActive(true);

        from.interactable = false;
        from.blocksRaycasts = false;

        float t = 0f;
        float startFrom = from.alpha;
        float startFade = fadeCanvasGroup.alpha;

        while (t < fadeDuration)
        {
            t += Time.deltaTime;
            float norm = Mathf.Clamp01(t / fadeDuration);
            from.alpha = Mathf.Lerp(startFrom, 0f, norm);
            fadeCanvasGroup.alpha = Mathf.Lerp(startFade, 1f, norm);
            yield return null;
        }

        from.alpha = 0f;
        fadeCanvasGroup.alpha = 1f;

        // ---------- 여기에 SessionNameHelper를 이용한 검사 삽입 ----------
        bool shouldValidate = true;
        // (옵션) 필요하면 특정 from 화면 이름만 검사하도록 조건 추가 가능
        // e.g. shouldValidate = from.gameObject.name == "LoginScreen";

        if (shouldValidate && sessionNameHelper != null)
        {
            // SessionNameHelper에 현재 입력 검사 메서드를 추가했어야 함
            if (sessionNameHelper.IsCurrentInputProfane())
            {
                // 비속어 발견 -> 팝업 열기(모달)
                sessionNameHelper.ShowProfanityWarning();

                // 역페이드(검정 -> 원래 화면)
                float tb = 0f;
                float startFadeBack = fadeCanvasGroup.alpha; // 보통 1
                float startFromBack = from.alpha; // 0
                while (tb < fadeDuration)
                {
                    tb += Time.deltaTime;
                    float norm = Mathf.Clamp01(tb / fadeDuration);
                    fadeCanvasGroup.alpha = Mathf.Lerp(startFadeBack, 0f, norm); // 검정 1 -> 0
                    from.alpha = Mathf.Lerp(startFromBack, 1f, norm);           // from 0 -> 1
                    yield return null;
                }

                // 확실히 복구
                fadeCanvasGroup.alpha = 0f;
                from.alpha = 1f;
                fadeCanvasGroup.gameObject.SetActive(false);

                from.interactable = true;
                from.blocksRaycasts = true;

                isTransitioning = false;
                yield break; // 전환 취소
            }
        }
        // ---------- 검사 통과시 계속 진행 ----------

        from.gameObject.SetActive(false);

        to.gameObject.SetActive(true);
        to.alpha = 0f;
        to.interactable = false;
        to.blocksRaycasts = false;

        t = 0f;
        float startTo = to.alpha;
        float startFade2 = fadeCanvasGroup.alpha;

        while (t < fadeDuration)
        {
            t += Time.deltaTime;
            float norm = Mathf.Clamp01(t / fadeDuration);
            fadeCanvasGroup.alpha = Mathf.Lerp(startFade2, 0f, norm);
            to.alpha = Mathf.Lerp(startTo, 1f, norm);
            yield return null;
        }

        fadeCanvasGroup.alpha = 0f;
        to.alpha = 1f;
        to.interactable = true;
        to.blocksRaycasts = true;

        fadeCanvasGroup.interactable = false;
        fadeCanvasGroup.blocksRaycasts = false;
        fadeCanvasGroup.gameObject.SetActive(false);

        isTransitioning = false;
    }


    private IEnumerator SyncLocalPlayerNameAfterJoin()
    {
        string playerName = null;
        if (inputField != null && !string.IsNullOrWhiteSpace(inputField.text))
            playerName = inputField.text.Trim();
        else if (inputField2 != null && !string.IsNullOrWhiteSpace(inputField2.text))
            playerName = inputField2.text.Trim();
        else if (idInputField != null && !string.IsNullOrWhiteSpace(idInputField.text))
            playerName = idInputField.text.Trim();

        if (string.IsNullOrEmpty(playerName)) yield break;

        float timeout = 5f;
        float startTime = Time.realtimeSinceStartup;
        GameObject localPlayer = null;

        while ((localPlayer = NetworkManager.Singleton?.SpawnManager?.GetLocalPlayerObject()?.gameObject) == null &&
               Time.realtimeSinceStartup - startTime < timeout)
        {
            yield return new WaitForSeconds(0.1f);
        }

        if (localPlayer == null) yield break;

        var pm = localPlayer.GetComponent<PlayerMovement>();
        if (pm != null)
            pm.SetPlayerNameServerRpc(playerName);

        // UI에도 반영
        StartCoroutine(SaveLocalPlayerNameToSessionListCoroutine(4f, 5f, 0.1f));

        if (gameStartButton != null)
            gameStartButton.interactable = true;
    }


    public void UpdateAllPlayerNamesInUI()
    {
        if (sessionPlayerListContent == null) return;


        // 먼저 모든 slot UI를 초기화(혹은 재사용)
        // 여기서는 간단히 content child 수 만큼 순회하는 예시
        var content = sessionPlayerListContent;
        int uiIndex = 0;

        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var netObj = kv.Value;
            if (netObj == null) continue;

            var pm = netObj.GetComponent<PlayerMovement>();
            if (pm == null) continue;

            // 안전하게 string으로 변환
            string nameStr = pm.playerName.Value.ToString();



            // UI에 넣기 (content child가 부족하면 건너뜀 — 필요시 Instantiate logic 추가)
            if (uiIndex < content.childCount)
            {
                var item = content.GetChild(uiIndex);
                var nameTransform = item.Find("Row/Name Container/Player Name") ?? FindChildRecursive(item, "Player Name");
                if (nameTransform != null)
                    SetTextOnGameObject(nameTransform.gameObject, RemoveRichTextTags(nameStr));
                else
                {
                    var tmp = item.GetComponentInChildren<TMPro.TMP_Text>(true);
                    if (tmp != null) tmp.SetText(RemoveRichTextTags(nameStr));
                    else
                    {
                        var ut = item.GetComponentInChildren<UnityEngine.UI.Text>(true);
                        if (ut != null) ut.text = RemoveRichTextTags(nameStr);
                    }
                }
            }

            uiIndex++;
        }


        // 남는 UI 슬롯은 빈칸으로 처리
        for (int i = uiIndex; i < content.childCount; i++)
        {
            var item = content.GetChild(i);
            var tmp = item.GetComponentInChildren<TMPro.TMP_Text>(true);
            if (tmp != null) tmp.SetText("");
            else
            {
                var ut = item.GetComponentInChildren<UnityEngine.UI.Text>(true);
                if (ut != null) ut.text = "";
            }
        }
    }


    private void EnsureFadePanelOnTop()
    {
        if (fadeCanvasGroup == null) return;
        Canvas fadeCanvas = fadeCanvasGroup.GetComponentInParent<Canvas>();
        if (fadeCanvas != null)
        {
            fadeCanvas.transform.SetAsLastSibling();
            fadeCanvas.overrideSorting = true;
            fadeCanvas.sortingOrder = 1000;
        }
    }

    private void EnsureFullScreenRectTransform(CanvasGroup cg)
    {
        if (cg == null) return;
        var rt = cg.transform as RectTransform;
        if (rt == null) return;
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.anchoredPosition = Vector2.zero;
        rt.sizeDelta = Vector2.zero;
    }

    public void OnQuitButtonClicked()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit();
#endif
    }

    public async void OnCreateSessionClicked()
    {
        if (gameStartButton != null)
            gameStartButton.interactable = false;

        try
        {
            await Unity.Services.Core.UnityServices.InitializeAsync();
            if (!Unity.Services.Authentication.AuthenticationService.Instance.IsSignedIn)
                await Unity.Services.Authentication.AuthenticationService.Instance.SignInAnonymouslyAsync();

            // 방 생성
            var allocation = await Unity.Services.Relay.RelayService.Instance.CreateAllocationAsync(8);
            var relayData = Unity.Services.Relay.Models.AllocationUtils.ToRelayServerData(allocation, "dtls");

            // Transport 세팅
            var transport = NetworkManager.Singleton.GetComponent<Unity.Netcode.Transports.UTP.UnityTransport>();
            transport.SetRelayServerData(relayData);

            PlayerCharacterManager.ResetSlotAllocation();
            StartCoroutine(SaveLocalPlayerNameToSessionListCoroutine(4f, 5f, 0.1f));

            // UI 처리
            if (gameStartButton != null) gameStartButton.gameObject.SetActive(true);

            UpdateCharacterSlots();

        }
        finally
        {
            if (gameStartButton != null) gameStartButton.interactable = false;
        }

    }

    public void OnJoinSessionClicked()
    {
        // 2) 서버에 비밀번호를 요청 (async / 네트워크 안정화 필요 없음)
        if (PasswordVisibilityNetwork.Instance != null)
        {
            // 콜백 등록
            PasswordVisibilityNetwork.Instance.OnPasswordReceived += HandlePasswordReceived;
            // 서버 RPC 호출 (서버가 SendPasswordDirectlyClientRpc 으로 callback 해 줌)
            PasswordVisibilityNetwork.Instance.RequestPasswordFromServerRpc();
        }

        // 비밀번호가 없거나 PasswordPromptUI가 없으면 바로 Join 진행
        ContinueJoinAfterPassword();

        // 서버 입장 시 바로 슬롯 갱신
        if (IsServer) UpdateCharacterSlots();
    }

    [ClientRpc]
    private void NotifyKickedClientClientRpc(ClientRpcParams rpcParams = default)
    {
        if (IsHost) return;

        Debug.Log("서버로부터 강퇴당했습니다. 게임을 종료합니다.");

        if (nextCanvasGroups.Length > 2 && nextCanvasGroups[2] != null)
        {
            // 모든 UI를 숨기고 강퇴 화면만 표시
            foreach (var cg in currentCanvasGroups)
                if (cg != null) cg.gameObject.SetActive(false);
            foreach (var cg in nextCanvasGroups)
                if (cg != null && cg != nextCanvasGroups[2]) cg.gameObject.SetActive(false);

            // 강퇴 화면만 활성화
            nextCanvasGroups[2].gameObject.SetActive(true);
            nextCanvasGroups[2].alpha = 1f;
            nextCanvasGroups[2].interactable = true;
            nextCanvasGroups[2].blocksRaycasts = true;
        }

        if (exitButtonCanvasGroup != null)
        {
            exitButtonCanvasGroup.alpha = 1f;
            exitButtonCanvasGroup.interactable = true;
            exitButtonCanvasGroup.blocksRaycasts = true;
        }

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsConnectedClient)
        {
            NetworkManager.Singleton.Shutdown();
        }
    }

    private void OnExitButtonClicked()
    {
        Debug.Log($"OnExitButtonClicked() called. allowTransition={allowTransition}");
        if (exitButtonCanvasGroup != null)
        {
            exitButtonCanvasGroup.alpha = 1f;
            exitButtonCanvasGroup.interactable = false;
            exitButtonCanvasGroup.blocksRaycasts = false;
        }

        // 2) 원하는 CanvasGroup 으로 돌아가기 (exitScreenIndex)
        allowTransition = true;
        var routine = TransitionRoutine(currentCanvasGroups[2], nextCanvasGroups[2]);

        StartCoroutine(routine);

        NetworkManager.Singleton.Shutdown();
    }

    private void HandlePasswordReceived(string pwd)
    {
        // (A) 콜백 해제
        PasswordVisibilityNetwork.Instance.OnPasswordReceived -= HandlePasswordReceived;

        // (B) 받은 pwd 가 비어있지 않으면 모달 띄우기
        if (!string.IsNullOrEmpty(pwd) && PasswordPromptUI.Instance != null)
        {
            Debug.Log($"[후입장 클라이언트] Password Received: '{pwd}' → PromptUI 띄움");
            PasswordPromptUI.Instance.Show(
                "비밀번호를 입력하세요",
                pwd,
                // 성공 콜백
                ContinueJoinAfterPassword
            );
        }
        else
        {
            // (C) 비밀번호가 없으면 바로 Join
            Debug.Log("[후입장 클라이언트] 빈 비밀번호 → 바로 Join");
            ContinueJoinAfterPassword();
        }
    }

    private IEnumerator DelayedPasswordSync(string password, bool visible)
    {
        yield return new WaitForSeconds(4f); // 네트워크 안정화 대기

        if (PasswordVisibilityNetwork.Instance != null)
        {
            PasswordVisibilityNetwork.Instance.RequestInitialPasswordSyncServerRpc(password, visible);
            Debug.Log($"[DelayedPasswordSync] Synced password: '{password}', visible: {visible}");
        }


        // 추가: UI 상태 재확인 및 복원
        var ui = TogglePasswordUI.Instance;
        if (ui != null && ui.IsCurrentlyHost())
        {
            ui.RestoreStateAfterNetworkReconnect();
        }

    }

    // 기존 OnJoinSessionClicked 내부 로직을 여기에 옮겨 재사용
    private async void ContinueJoinAfterPassword()
    {
        if (gameStartButton != null) gameStartButton.gameObject.SetActive(false);

        try
        {
            await Unity.Services.Core.UnityServices.InitializeAsync();
            if (!Unity.Services.Authentication.AuthenticationService.Instance.IsSignedIn)
                await Unity.Services.Authentication.AuthenticationService.Instance.SignInAnonymouslyAsync();

            // 접속 후 이름 동기화 대기
            StartCoroutine(SyncLocalPlayerNameAfterJoin());

            // 클라이언트 접속 이벤트 구독
            NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnectedForJoin;
        }
        finally
        {
            if (gameStartButton != null)
                gameStartButton.interactable = false;
        }

        // LocalPlayer가 생성될 때까지 코루틴으로 기다리고 이름 동기화
        StartCoroutine(SyncLocalPlayerNameAfterJoin());

        NetworkManager.Singleton.OnClientConnectedCallback += OnClientConnected;
    }

    private void OnClientConnectedForJoin(ulong clientId)
    {
        if (clientId != NetworkManager.Singleton.LocalClientId) return;

        // 이벤트 구독 해제
        NetworkManager.Singleton.OnClientConnectedCallback -= OnClientConnectedForJoin;

        RegisterPlayer(NetworkManager.Singleton.SpawnManager.GetPlayerNetworkObject(clientId).gameObject);
    }

    private void OnClientConnected(ulong clientId)
    {
        if (!IsServer) return;

        Debug.Log($"[UIScreenTransitionManager] Client {clientId} connected");

        var netObj = NetworkManager.Singleton.SpawnManager.GetPlayerNetworkObject(clientId);
        if (netObj != null)
        {
            RegisterPlayer(netObj.gameObject);

            // PlayerCharacterManager에게 새 플레이어 추가
            var pcm = FindFirstObjectByType<PlayerCharacterManager>();
            if (pcm != null)
            {
                pcm.AddPlayer(clientId, netObj.NetworkObjectId);
            }
        }
    }

    private void OnClientDisconnected(ulong clientId)
    {
        if (!IsServer) return;

        Debug.Log($"[UIScreenTransitionManager] Client {clientId} disconnected");

        var netObj = NetworkManager.Singleton.SpawnManager.GetPlayerNetworkObject(clientId);
        if (netObj != null)
        {
            UnregisterPlayer(netObj.gameObject);

            // PlayerCharacterManager에게 플레이어 제거 요청
            var pcm = FindFirstObjectByType<PlayerCharacterManager>();
            if (pcm != null)
            {
                pcm.RemovePlayer(netObj.NetworkObjectId);
            }
        }
    }


    private void UpdateCharacterSlots()
    {
        // 이 메서드는 더 이상 필요하지 않음
        // PlayerCharacterManager가 자동으로 관리함
        Debug.Log("[UIScreenTransitionManager] UpdateCharacterSlots called (deprecated)");
    }


    private IEnumerator SaveLocalPlayerNameToSessionListCoroutine(float initialDelay = 0.5f, float timeout = 5f, float pollInterval = 0.1f)
    {
        string playerName = null;
        if (inputField != null && !string.IsNullOrWhiteSpace(inputField.text))
            playerName = inputField.text.Trim();
        else if (inputField2 != null && !string.IsNullOrWhiteSpace(inputField2.text))
            playerName = inputField2.text.Trim();
        else if (idInputField != null && !string.IsNullOrWhiteSpace(idInputField.text))
            playerName = idInputField.text.Trim();

        if (string.IsNullOrEmpty(playerName)) yield break;

        if (initialDelay > 0f) yield return new WaitForSeconds(initialDelay);

        Transform content = sessionPlayerListContent;

        float startTime = Time.realtimeSinceStartup;
        while (content == null && Time.realtimeSinceStartup - startTime < timeout)
        {
            var sessionListGO = GameObject.Find("Session Player List");
            if (sessionListGO != null)
            {
                var sr = sessionListGO.GetComponentInChildren<ScrollRect>(true);
                if (sr != null && sr.content != null)
                {
                    content = sr.content;
                    break;
                }

                var ct = sessionListGO.transform.Find("Content");
                if (ct != null)
                {
                    content = ct;
                    break;
                }
            }

            var roots = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            foreach (var r in roots)
            {
                var maybe = r.transform.Find("Session Player List/Content");
                if (maybe != null)
                {
                    content = maybe;
                    break;
                }
            }

            if (content == null) yield return new WaitForSeconds(pollInterval);
        }

        yield return null;

        // 안전하게 child 탐색 및 텍스트 세팅
        for (int i = 0; i < content.childCount; i++)
        {
            var item = content.GetChild(i);
            if (item == null) continue;

            var playerNameTransform = item.Find("Row/Name Container/Player Name");
            if (playerNameTransform == null)
                playerNameTransform = FindChildRecursive(item, "Player Name");

            if (playerNameTransform != null)
            {
                // 여기에 들어가는 playerName은 이미 string 이므로 바로 사용
                SetTextOnGameObject(playerNameTransform.gameObject, playerName);
                break;
            }

            var tmp = item.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null)
            {
                tmp.SetText(playerName);
                break;
            }
            var ut = item.GetComponentInChildren<Text>(true);
            if (ut != null)
            {
                ut.text = playerName;
                break;
            }
        }

        // 서버에 실제로 전송 (LocalPlayer가 준비된 경우)
        if (NetworkManager.Singleton.IsConnectedClient && NetworkManager.Singleton.SpawnManager != null)
        {
            var localPlayer = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
            if (localPlayer != null)
            {
                var pm = localPlayer.GetComponent<PlayerMovement>();
                if (pm != null)
                    pm.SetPlayerNameServerRpc(playerName); // 서버에게 자신의 이름 전송
            }
        }
    }


    private Transform FindChildRecursive(Transform parent, string childName)
    {
        if (parent == null) return null;
        for (int i = 0; i < parent.childCount; i++)
        {
            var ch = parent.GetChild(i);
            if (string.Equals(ch.name, childName, System.StringComparison.OrdinalIgnoreCase))
                return ch;
            var deeper = FindChildRecursive(ch, childName);
            if (deeper != null) return deeper;
        }
        return null;
    }

    private void SetTextOnGameObject(GameObject target, string text)
    {
        if (target == null) return;
        string safe = text ?? "";

        // 항상 명시적 ToString 결과만 사용 (이 함수에 들어오는 text는 이미 string이어야 함)
        // TMP_Text (TextMesh Pro) 처리
        var tmp = target.GetComponent<TMP_Text>();
        if (tmp != null)
        {
            tmp.SetText(safe); // 명시적 string 오버로드 사용
            return;
        }

        // UnityEngine.UI.Text 처리
        var uiText = target.GetComponent<Text>();
        if (uiText != null)
        {
            uiText.text = safe;
            return;
        }

        // TextMesh (3D 텍스트) 등 다른 타입의 경우도 처리 가능
        var mesh = target.GetComponent<TextMesh>();
        if (mesh != null)
        {
            mesh.text = safe;
        }
    }

    // 네임스페이스 체크용 헬퍼
    private bool HasMultiplayerWidgets(GameObject go)
    {
        var comps = go.GetComponentsInChildren<MonoBehaviour>(true);
        foreach (var c in comps)
        {
            if (c == null) continue;
            var fullname = c.GetType().FullName;
            if (fullname != null && fullname.StartsWith("Unity.Multiplayer.Widgets"))
                return true;
        }
        return false;
    }


    private void SafeSetActive(GameObject go, bool active)
    {
        if (go == null) return;

        bool hasWidget = HasMultiplayerWidgets(go);

        if (!hasWidget)
        {
            // 평범한 경우는 그냥 SetActive
            go.SetActive(active);
            return;
        }

        // 위젯이 포함되어 있으면 활성화(active==true) 시 직접 SetActive를 피한다.
        if (active)
        {
            // 시도 1: 이미 씬에 active 상태로 존재하지만 숨겨졌다면 CanvasGroup으로 나타냄
            var cg = go.GetComponent<CanvasGroup>() ?? go.GetComponentInChildren<CanvasGroup>(true);
            if (cg != null)
            {
                // GameObject가 이미 active라면 alpha 조절로 보이게
                if (cg.gameObject.activeInHierarchy)
                {
                    cg.alpha = 1f;
                    cg.interactable = true;
                    cg.blocksRaycasts = true;
                    return;
                }
            }
            return;
        }
        else
        {
            // 비활성화는 비교적 안전: 먼저 위젯 컴포넌트 비활성화 후 SetActive(false)
            var comps = go.GetComponentsInChildren<MonoBehaviour>(true);
            foreach (var c in comps)
            {
                if (c == null) continue;
                var fullname = c.GetType().FullName;
                if (fullname != null && fullname.StartsWith("Unity.Multiplayer.Widgets"))
                {
                    c.enabled = false; // 위젯 컴포넌트 끔
                }
            }
            go.SetActive(false);
        }
    }

    private string RemoveRichTextTags(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        var sb = new StringBuilder(text.Length);
        int idx = 0;
        while (idx < text.Length)
        {
            char c = text[idx];
            if (c == '<')
            {
                // '<' 시작이면 '>' 까지 스킵
                int end = text.IndexOf('>', idx);
                if (end >= 0)
                    idx = end + 1;
                else
                    break; // 닫는 '>'가 없으면 종료
            }
            else
            {
                sb.Append(c);
                idx++;
            }
        }

        return sb.ToString();
    }
}

