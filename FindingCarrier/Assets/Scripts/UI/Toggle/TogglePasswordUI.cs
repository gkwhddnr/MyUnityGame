using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.SceneManagement;

public class TogglePasswordUI : MonoBehaviour
{
    public static TogglePasswordUI Instance { get; private set; }

    [Header("UI References")]
    [Tooltip("비밀번호 보이기 토글 (항상 보이도록 하세요)")]
    public Toggle passwordToggle;

    [Tooltip("사용자가 입력하는 비밀번호 InputField")]
    public TMP_InputField passwordInputFieldObject;

    [Tooltip("씬에 있는 Password 표시용 TMP_InputField (PasswordBox 안)")]
    public TMP_InputField passwordDisplay;

    [Header("초기 설정")]
    [Tooltip("토글의 초기 체크 상태 (기본 false -> 꺼짐)")]
    public bool startChecked = false;

    private bool suppressToggleCallback = false;
    private bool isHost = false; // 방장 여부 추적
    private bool hasBeenInitialized = false;

    private static string savedPassword = "";
    private static bool lastToggleState = false;
    private static bool persistentHostState = false;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject); // 필요 없으면 제거 가능
        }
        else
        {
            Destroy(gameObject);
        }
        passwordToggle.onValueChanged.AddListener(OnToggleValueChanged);
    }

    private void Start()
    {
        // 초기 상태 설정
        if (passwordToggle != null && !hasBeenInitialized)
        {
            suppressToggleCallback = true;

            // 방장 상태가 유지되고 있다면 그 상태를 복원
            if (persistentHostState)
            {
                passwordToggle.isOn = lastToggleState;
            }
            else
            {
                passwordToggle.isOn = false;
                lastToggleState = false;
            }

            passwordInputFieldObject?.gameObject.SetActive(passwordToggle.isOn);
            suppressToggleCallback = false;
            hasBeenInitialized = true;
        }
    }

    private void OnEnable()
    {
        suppressToggleCallback = true;
        if (passwordToggle != null)
        {
            passwordToggle.isOn = lastToggleState;
            passwordInputFieldObject?.gameObject.SetActive(lastToggleState);
        }
        ApplyPasswordToDisplayIfToggled();
        suppressToggleCallback = false;

        if (passwordToggle != null && passwordToggle.isOn)
        {
            ApplyPasswordToDisplayIfToggled();
        }
    }

    private void OnDisable()
    {
        if (passwordToggle != null)
            passwordToggle.onValueChanged.RemoveListener(OnToggleValueChanged);
    }

    private void OnDestroy()
    {
        // 안전하게 제거 (중복 해제 방지)
        if (passwordToggle != null)
            passwordToggle.onValueChanged.RemoveListener(OnToggleValueChanged);
    }

    private void OnToggleValueChanged(bool isOn)
    {
        if (suppressToggleCallback) return;

        lastToggleState = isOn;

        if (!isOn)
        {
            passwordInputFieldObject.gameObject.SetActive(false);
            if (passwordToggle != null)
                passwordToggle.isOn = false;
        }
        // 토글이 'true'가 되려고 한다면,
        // 비밀번호 입력 필드를 활성화
        else
        {
            passwordInputFieldObject.gameObject.SetActive(true);
        }

        ApplyVisibilityLocal(isOn);

        // 네트워크 동기화
        PasswordVisibilityNetwork.Instance?.RequestSetPasswordVisibility(isOn);
    }

    public void SetAsHost(bool hostState, bool passwordToggleState)
    {
        isHost = hostState;
        persistentHostState = hostState;

        suppressToggleCallback = true;

        if (passwordToggle != null)
        {
            // 방장인 경우: passwordToggleState 값에 따라 설정
            // 후입장인 경우: false로 설정
            bool finalState = hostState ? passwordToggleState : false;
            passwordToggle.isOn = finalState;
            lastToggleState = finalState;

            // 비밀번호도 방장이 아니면 초기화
            if (!hostState)
            {
                savedPassword = "";
                if (passwordInputFieldObject != null)
                    passwordInputFieldObject.text = "";
            }
        }

        if (passwordInputFieldObject != null)
        {
            passwordInputFieldObject.gameObject.SetActive(passwordToggle != null ? passwordToggle.isOn : false);
        }

        suppressToggleCallback = false;
        hasBeenInitialized = true;

        Debug.Log($"[TogglePasswordUI] SetAsHost called - isHost: {hostState}, toggleState: {passwordToggleState}, final toggle.isOn: {passwordToggle?.isOn}");
    }

    public void ApplyVisibilityLocal(bool visible)
    {
        suppressToggleCallback = true;

        if (passwordToggle != null)
        {
            passwordToggle.isOn = visible;
            lastToggleState = visible;
        }

        if (passwordInputFieldObject != null)
            passwordInputFieldObject.gameObject.SetActive(visible);

        if (!visible && passwordDisplay != null)
            passwordDisplay.text = "";

        suppressToggleCallback = false;
    }

    // 버튼에서 호출: 토글이 켜져있으면 입력값을 복사
    public void ApplyPasswordToDisplayIfToggled()
    {
        if (passwordDisplay == null || passwordToggle == null || passwordInputFieldObject == null) return;

        savedPassword = passwordInputFieldObject.text;

        // Toggle이 비활성화 상태라면 lastToggleState를 참조
        bool shouldShow = passwordToggle.gameObject.activeInHierarchy ? passwordToggle.isOn : lastToggleState;

        if (shouldShow && !string.IsNullOrEmpty(savedPassword))
        {
            passwordDisplay.text = savedPassword;
        }

        else passwordDisplay.text = "";
    }

    public void ClearPasswordDisplay()
    {
        if (passwordDisplay != null)
            passwordDisplay.text = "";

        if (passwordInputFieldObject != null)
            passwordInputFieldObject.text = "";

        savedPassword = "";
        lastToggleState = false;

        suppressToggleCallback = true;
        if (passwordToggle != null)
            passwordToggle.isOn = false;
        suppressToggleCallback = false;
    }

    public bool GetCurrentToggleState()
    {
        return passwordToggle != null && passwordToggle.gameObject.activeInHierarchy ? passwordToggle.isOn : lastToggleState;
    }

    // 저장된 비밀번호 반환
    public string GetSavedPassword()
    {
        return savedPassword;
    }

    public bool IsCurrentlyHost()
    {
        return persistentHostState;
    }

    // 네트워크 재연결 시 상태 복원
    public void RestoreStateAfterNetworkReconnect()
    {
        if (persistentHostState && !string.IsNullOrEmpty(savedPassword))
        {
            suppressToggleCallback = true;
            if (passwordToggle != null)
            {
                passwordToggle.isOn = lastToggleState;
            }
            if (passwordDisplay != null && lastToggleState)
            {
                passwordDisplay.text = savedPassword;
            }
            suppressToggleCallback = false;

            Debug.Log($"[TogglePasswordUI] State restored - toggle: {lastToggleState}, password: '{savedPassword}'");
        }
    }
}
