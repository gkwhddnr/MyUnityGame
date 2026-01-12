using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[RequireComponent(typeof(CanvasGroup))]
public class ChatInputHandler : MonoBehaviour
{
    [Header("UI References")]
    public CanvasGroup chatCanvasGroup;    // ChatToggleOnEnter가 제어하는 CanvasGroup
    public TMP_InputField inputField;      // Message Input Field (TMP_InputField)
    public Button submitButton;            // Message Submit 버튼

    [Header("Message Display")]
    public Transform contentRoot;          // Scroll View -> Viewport -> Content
    public GameObject messagePrefab;       // 메시지 프리팹 (안에 TMP_Text)

    private List<string> profanityList = new List<string> { "시발", "씨발", "애미", "개새끼", "병신", "ㅄ", "ㅅㅂ",
     "섹스", "보지", "자지", "장애인", "새끼", "년" , "놈", "좆", "ㅈ", "ㄴㅇㅁ", "니엄마", "등신", "아가리", "엿", 
    "꼬추", "sex", "fuck", "bitch", "suck", "젖", "씹", "존나"};
    
    private char maskChar = '*';

    private bool useWholeWordMatching = true;
    private bool autoEnableInputWhenOpen = true; // CanvasGroup 열리면 InputField.interactable = true
    private bool clearInputAfterSend = true;

    void Start()
    {
        if (chatCanvasGroup == null) chatCanvasGroup = GetComponent<CanvasGroup>();

        if (inputField != null)
        {
            // remove any problematic onEndEdit handlers (optional safeguard)
            inputField.onEndEdit.RemoveAllListeners();
            inputField.onValueChanged.AddListener(_ => UpdateSubmitButtonInteractable());

            inputField.onEndEdit.RemoveAllListeners();
        }

        if (submitButton != null)
        {
            submitButton.onClick.RemoveAllListeners();
            submitButton.onClick.AddListener(() => TrySubmitFromInput());
        }

        ApplyCanvasGroupState();
        UpdateSubmitButtonInteractable();
    }

    void OnDestroy()
    {
        if (inputField != null)
        {
            inputField.onValueChanged.RemoveAllListeners();
            inputField.onSubmit.RemoveAllListeners();
        }
        if (submitButton != null) submitButton.onClick.RemoveAllListeners();
    }

    void Update()
    {
        // CanvasGroup이 열려있을 때만 입력 활성화 관련 처리
        bool isOpen = chatCanvasGroup != null && chatCanvasGroup.alpha > 0.5f;

        if (autoEnableInputWhenOpen && inputField != null)
        {
            inputField.interactable = isOpen;
            // show/hide the inputField GameObject to match ChatToggleOnEnter behavior
            if (inputField.gameObject.activeSelf != isOpen)
                inputField.gameObject.SetActive(isOpen);
        }

        // Enter 키로 전송: 입력필드에 포커스 있고 IME 조합중이 아니어야 함
        if (inputField != null && inputField.isFocused)
        {
            UpdateSubmitButtonInteractable();

            // Enter 키로 전송: 입력필드에 포커스 있고 IME 조합중이 아니어야 함
            if (string.IsNullOrEmpty(Input.compositionString))
            {
                if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
                {
                    // TrySubmitFromInput는 bool을 반환(전송 성공 여부)
                    bool sent = TrySubmitFromInput();
                    // sent == true => 전송했고 입력은 비워졌음. (토글/숨김 로직은 ChatToggleOnEnter가 처리)
                }
            }
        }
    }

    private void UpdateSubmitButtonInteractable()
    {
        string reliable = GetReliableInputText();
        bool has = !string.IsNullOrWhiteSpace(reliable);
        if (submitButton != null) submitButton.interactable = has;
    }

    private void OnInputValueChanged(string value)
    {
        if (submitButton != null)
        {
            submitButton.interactable = !string.IsNullOrEmpty(value.Trim());
        }
    }

    public string GetReliableInputText()
    {
        if (inputField == null) return string.Empty;

        // 1) 우선 inputField.text
        string txt = inputField.text ?? "";

        // 2) 비어 있거나 의심될 경우 textComponent에서 읽기 (TMP 렌더 텍스트)
        if (string.IsNullOrWhiteSpace(txt) && inputField.textComponent != null)
        {
            // ForceMeshUpdate로 visual text를 즉시 갱신
            inputField.textComponent.ForceMeshUpdate();
            txt = inputField.textComponent.text ?? "";
        }

        return txt.Trim();
    }

    private void OnSubmitClicked()
    {
        TrySubmitFromInput();
    }

    public bool TrySubmitFromInput()
    {
        if (inputField == null) return false;

        // IME 조합 중이면 포커스 유지
        if (!string.IsNullOrEmpty(Input.compositionString))
        {
            inputField.ActivateInputField();
            return false;
        }

        string text = GetReliableInputText();
        if (string.IsNullOrWhiteSpace(text))
        {
            inputField.text = "";
            inputField.ActivateInputField();
            return false;
        }

        string filtered = FilterProfanity(text);

        // 네트워크 전송: 로컬 플레이어의 PlayerMovement ServerRpc 호출
        var nm = Unity.Netcode.NetworkManager.Singleton;
        if (nm != null && nm.SpawnManager != null)
        {
            var local = nm.SpawnManager.GetLocalPlayerObject();
            var pm = local?.GetComponent<PlayerMovement>();
            if (pm != null)
            {
                pm.SubmitMessageServerRpc(filtered);
            }
        }

        if (clearInputAfterSend)
        {
            inputField.text = "";
            if (inputField.textComponent != null)
                inputField.textComponent.ForceMeshUpdate();
            inputField.ActivateInputField();
            EventSystem.current?.SetSelectedGameObject(inputField.gameObject);
        }

        // submit 버튼 상태 갱신
        UpdateSubmitButtonInteractable();

        return true;
    }


    private void AppendLocalMessage(string message)
    {
        if (contentRoot == null || messagePrefab == null) return;

        var go = Instantiate(messagePrefab, contentRoot);
        go.SetActive(true);

        var tmp = go.GetComponentInChildren<TMP_Text>();
        if (tmp != null)
        {
            tmp.text = message;
        }

        Canvas.ForceUpdateCanvases();
        var sr = contentRoot.GetComponentInParent<ScrollRect>();
        if (sr != null)
        {
            sr.verticalNormalizedPosition = 0f;
        }
    }

    private void ApplyCanvasGroupState()
    {
        if (chatCanvasGroup == null) return;

        bool isOpen = chatCanvasGroup.alpha > 0.5f;
        if (inputField != null)
        {
            inputField.interactable = isOpen;
            inputField.gameObject.SetActive(isOpen);
        }

        if (submitButton != null)
            submitButton.interactable = isOpen && !string.IsNullOrWhiteSpace(inputField?.text);

        if (chatCanvasGroup != null)
        {
            chatCanvasGroup.interactable = isOpen;
            chatCanvasGroup.blocksRaycasts = isOpen;
        }
    }

    private string FilterProfanity(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        string output = input;

        // 빠른 실패
        if (profanityList == null || profanityList.Count == 0) return output;

        foreach (var raw in profanityList)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            string word = raw.Trim();

            string pattern;

            if (useWholeWordMatching && IsAscii(word)) pattern = $@"(?i)\b{Regex.Escape(word)}\b";
            else pattern = $@"(?i){Regex.Escape(word)}";
            

            // 금칙어 자체만 동일 길이의 마스크로 대체 (주변 텍스트는 그대로)
            output = Regex.Replace(output, pattern, m =>
            {
                return new string(maskChar, m.Value.Length);
            });
        }

        return output;
    }

    private bool IsAscii(string s)
    {
        // 단순 검사: 모든 문자가 ASCII(0..127)이면 true
        return s.All(c => c <= 127);
    }
}
