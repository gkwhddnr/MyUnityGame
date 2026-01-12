using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Netcode;

public class SessionNameHelper : MonoBehaviour
{
    [Header("Login / 이름 입력")]
    public TMP_InputField inputField;            // 로그인에서 입력한 이름
    public Button loginButton;                   // 로그인 버튼 (OnClick에 OnLoginPressed 연결)

    [Header("Session Player List root (Inspector에 할당 권장)")]
    [Tooltip("Session Player List의 Content(또는 그 하이라키 루트)를 할당하세요.")]
    public Transform sessionListRoot;
    private string sessionListRootNameFallback = "Session Player List"; // 대체 이름

    [Header("Popup (비속어/빈값 경고)")]
    public GameObject popupPanel;                // 팝업 패널 (모달)
    public TextMeshProUGUI popupTextTMP;         // 팝업 텍스트 (TMP)
    public Button popupCloseButton;              // 닫기 버튼(선택)

    private bool ignoreWhitespaceWhenChecking = true; // 공백 제거 후 검사
    private List<string> profanityList = new List<string>
    {
        "시발","씨발","애미","개새끼","병신","ㅄ","ㅅㅂ",
        "섹스","보지","자지","장애인","새끼","년","놈","좆","ㅈ","ㄴㅇㅁ","니엄마",
        "등신","아가리","엿","꼬추","sex","fuck","bitch","suck","젖","씹", "닥쳐", "ㄷㅊ"
    };

    // 로컬에 저장(패브릭 복제 후 덮어쓰기용)
    private string localPlayerName;

    void Awake()
    {
        if (loginButton != null)
            loginButton.onClick.AddListener(OnLoginPressed);

        if (popupPanel != null)
            popupPanel.SetActive(false);

        if (popupCloseButton != null)
        {
            popupCloseButton.onClick.RemoveAllListeners();
            popupCloseButton.onClick.AddListener(() => HidePopup());
        }
    }

    // 로그인 버튼 눌렀을 때 호출 (비속어 검사 포함)
    public async void OnLoginPressed()
    {
        string rawName = inputField != null ? inputField.text : string.Empty;
        string nameTrimmed = rawName?.Trim();

        // 1) 빈 입력 체크
        if (string.IsNullOrEmpty(nameTrimmed))
        {
            ShowPopup("아이디를 입력하세요.");
            return;
        }

        // 2) 비속어 검사 (공백 제거 옵션 적용)
        string checkTarget = nameTrimmed;
        if (ignoreWhitespaceWhenChecking)
            checkTarget = RemoveWhitespace(checkTarget);

        string found = CheckProfanity(checkTarget);
        if (found != null)
        {
            // 사용자가 요청한 정확한 문구로 모달 경고 출력하고 진행 중단
            ShowPopup("<color=red>[알림]\n 아이디 속에 비속어나 음란어가 섞여있습니다.\n 다른 아이디로 만드세요!</color>");
            return;
        }

        // 3) 통과하면 이후 기존 흐름 실행
        localPlayerName = nameTrimmed;

        // 1) Unity Services 초기화 & 익명 로그인(필요 시)
        try
        {
            if (UnityServices.State != ServicesInitializationState.Initialized)
                await UnityServices.InitializeAsync();

            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SessionNameHelper] Unity Services / Auth 초기화 실패: {ex.Message}");
            // 그래도 로컬 대체는 진행
        }

        // 2) 시도: Authentication 서비스에 플레이어 이름 업데이트
        try
        {
            if (AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.UpdatePlayerNameAsync(localPlayerName);
                Debug.Log($"[SessionNameHelper] Authentication playername 업데이트 성공: {localPlayerName}");
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[SessionNameHelper] UpdatePlayerNameAsync 실패 (권한/403 가능): {ex.Message}");
        }

        // 3) 로컬 저장 (RelaySessionManager 같은 곳에도 저장할 수 있음)
        RelaySessionManager.Instance?.SetLocalPlayerName(localPlayerName);

        // 4) 방 생성/입장 이후에 적용되도록 리스트에 덮어쓰기 시도
        StartCoroutine(ApplyLocalNameToSessionListDelayed());

        var transitionMgr = FindFirstObjectByType<UIScreenTransitionManager>();
        if (transitionMgr != null)
        {
            transitionMgr.EnableTransition();
            transitionMgr.OnTransitionButtonClicked(0); // index는 UI 구조에 맞게 설정
        }
    }

    // profanityList 중 포함되는 단어를 반환. 없으면 null.
    private string CheckProfanity(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;

        string lower = text.ToLowerInvariant();
        foreach (var bad in profanityList)
        {
            if (string.IsNullOrEmpty(bad)) continue;
            string badLower = bad.ToLowerInvariant();
            if (lower.IndexOf(badLower, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return bad;
            }
        }
        return null;
    }

    private static string RemoveWhitespace(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            if (!char.IsWhiteSpace(c)) sb.Append(c);
        return sb.ToString();
    }

    // 방 생성 직후(혹은 참가 직후) 호출해도 됨. 여기서는 로그인 직후 안전하게 호출.
    private IEnumerator ApplyLocalNameToSessionListDelayed(float delay = 0.25f)
    {
        yield return new WaitForSeconds(delay);

        if (sessionListRoot == null)
        {
            var go = GameObject.Find(sessionListRootNameFallback);
            if (go != null) sessionListRoot = go.transform;
        }

        if (sessionListRoot == null) yield break;

        var itemTransforms = sessionListRoot.GetComponentsInChildren<Transform>(true);

        int changed = 0;
        foreach (var item in itemTransforms)
        {
            var nameObj = item.Find("Row/Name Container/Player Name");
            if (nameObj == null)
                nameObj = item.Find("Player Name");
            if (nameObj != null)
            {
                var tmp = nameObj.GetComponent<TMP_Text>();
                if (tmp != null)
                {
                    if (ShouldReplacePlayerName(tmp.text))
                    {
                        tmp.text = localPlayerName;
                        changed++;
                    }
                }
            }
            else
            {
                var tmp2 = item.GetComponentInChildren<TMP_Text>(true);
                if (tmp2 != null && ShouldReplacePlayerName(tmp2.text))
                {
                    tmp2.text = localPlayerName;
                    changed++;
                }
            }
        }

        Debug.Log($"[SessionNameHelper] Session list에 로컬 이름 적용 시도. 변경된 항목 수: {changed}");
    }

    private bool ShouldReplacePlayerName(string existing)
    {
        if (string.IsNullOrEmpty(existing)) return true;
        if (existing.Contains("#")) return true;
        return false;
    }

    // 팝업 표시/숨김
    private void ShowPopup(string message)
    {
        if (popupPanel == null) return;
        if (popupTextTMP != null) popupTextTMP.text = message;
        popupPanel.SetActive(true);
    }

    private void HidePopup()
    {
        if (popupPanel != null) popupPanel.SetActive(false);
    }

    // SessionNameHelper 내부에 추가
    // (기존 using / 멤버 그대로 유지)

    /// <summary>
    /// 현재 inputField에 입력된 텍스트가 비속어/음란어를 포함하는지 검사.
    /// 인스펙터에서 설정한 ignoreWhitespaceWhenChecking 옵션을 사용.
    /// </summary>
    public bool IsCurrentInputProfane()
    {
        string raw = inputField != null ? inputField.text ?? string.Empty : string.Empty;
        string target = ignoreWhitespaceWhenChecking ? RemoveWhitespace(raw) : raw;
        return CheckProfanity(target) != null;
    }

    /// <summary>
    /// 주어진 문자열이 비속어/음란어를 포함하는지 검사(공백 옵션 따름).
    /// 외부에서 필요하면 직접 호출 가능.
    /// </summary>
    public bool IsNameProfane(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string target = ignoreWhitespaceWhenChecking ? RemoveWhitespace(name) : name;
        return CheckProfanity(target) != null;
    }

    /// <summary>
    /// SessionNameHelper의 팝업을 통해 비속어 경고 메시지를 띄움.
    /// (TransitionRoutine에서 팝업 띄우려면 이걸 호출하면 됨)
    /// </summary>
    public void ShowProfanityWarning()
    {
        ShowPopup("[알림] : 아이디 속에 비속어나 음란어가 섞여있습니다. 다른 아이디로 만드세요!");
    }

    /// <summary>
    /// 입력값을 외부에서 읽고 싶을 때 사용 (trim 적용).
    /// </summary>
    public string GetCurrentInputTrimmed()
    {
        return inputField != null ? (inputField.text ?? string.Empty).Trim() : string.Empty;
    }

}
