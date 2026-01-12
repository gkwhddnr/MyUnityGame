using UnityEngine;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using System.Collections;

[RequireComponent(typeof(CanvasGroup))]
public class ChatToggleOnEnter : MonoBehaviour
{
    [Header("UI References")]
    public CanvasGroup chatCanvasGroup;
    public GameObject chatInputGO;
    public TMP_InputField inputField;
    public Button submitButton;

    [Header("Behavior")]
    public KeyCode toggleKey = KeyCode.Return;
    public bool submitOnEnterWhenOpen = true;
    public float fadeDuration = 0.08f; // 0이면 즉시 열림

    bool visible = false;
    Coroutine fadeCoroutine;

    private void Reset()
    {
        if (chatCanvasGroup == null) chatCanvasGroup = GetComponent<CanvasGroup>();
    }

    private void Start()
    {
        chatCanvasGroup = GetComponentInChildren<CanvasGroup>();
        chatCanvasGroup.alpha = 0f;
        visible = false;

        chatCanvasGroup.interactable = false;
        chatCanvasGroup.blocksRaycasts = false;
        if (chatInputGO != null)
            chatInputGO.SetActive(false);
    }

    private void Update()
    {
        if (!string.IsNullOrEmpty(Input.compositionString)) return;

        // ChatToggleOnEnter.cs Update() 엔터 처리 부분 예시
        if (Input.GetKeyDown(toggleKey) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            if (visible && inputField != null && inputField.isFocused)
            {
                string buffer = GetReliableInputText();
                if (!string.IsNullOrWhiteSpace(buffer))
                {
                    // ChatInputHandler를 찾아서 전송 처리
                    var handler = GetComponentInChildren<ChatInputHandler>() ?? GetComponent<ChatInputHandler>();
                    if (handler != null)
                    {
                        bool sent = handler.TrySubmitFromInput();
                        if (sent)
                        {
                            // 전송 성공: 창을 닫지 않고 포커스 유지
                            inputField.Select();
                            inputField.ActivateInputField();
                            EventSystem.current?.SetSelectedGameObject(inputField.gameObject);
                        }
                        else
                        {
                            // 전송 실패(IME 등): 포커스 유지
                            inputField.ActivateInputField();
                        }
                    }
                }
                else
                {
                    // 내용이 없으면 창 닫음
                    InstantHide();
                }
                return;
            }

            ToggleVisibility(!visible);
        }
    }

    public void ToggleVisibility(bool show)
    {
        // 중간에 열리고 있으면 바로 멈춤
        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
            fadeCoroutine = null;
        }

        if (show)
        {
            fadeCoroutine = StartCoroutine(DoFadeIn());
        }
        else
        {
            InstantHide();
        }
    }

    private void InstantHide()
    {
        chatCanvasGroup.alpha = 0f;
        chatCanvasGroup.interactable = false;
        chatCanvasGroup.blocksRaycasts = false;
        visible = false;

        if (EventSystem.current != null)
            EventSystem.current.SetSelectedGameObject(null);
        if (chatInputGO != null)
            chatInputGO.SetActive(false);
    }

    private IEnumerator DoFadeIn()
    {
        float start = chatCanvasGroup.alpha;
        float end = 1f;
        float time = 0f;

        chatCanvasGroup.interactable = true;
        chatCanvasGroup.blocksRaycasts = true;
        if (chatInputGO != null) chatInputGO.SetActive(true);

        if (fadeDuration <= 0f)
        {
            chatCanvasGroup.alpha = end;
        }
        else
        {
            while (time < fadeDuration)
            {
                time += Time.unscaledDeltaTime;
                chatCanvasGroup.alpha = Mathf.Lerp(start, end, time / fadeDuration);
                yield return null;
            }
            chatCanvasGroup.alpha = end;
        }

        visible = true;

        if (inputField != null)
        {
            inputField.Select();
            inputField.ActivateInputField();
            if (EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(inputField.gameObject);
        }

        fadeCoroutine = null;
    }

    // 안정적으로 입력 텍스트를 얻는 헬퍼
    private string GetReliableInputText()
    {
        if (inputField == null) return string.Empty;

        // 우선 internal buffer (inputField.text) 우선 사용
        string txt = inputField.text ?? "";

        // 비어있거나 마지막 글자 누락 의심시 TMP visual에서 읽기
        if (string.IsNullOrWhiteSpace(txt) && inputField.textComponent != null)
        {
            inputField.textComponent.ForceMeshUpdate();
            txt = inputField.textComponent.text ?? "";
        }

        return txt.Trim();
    }

    // 코루틴 버전: 한 프레임 대기 후 전송 — TMP 타이밍 문제에 가장 안전한 방법
    private IEnumerator SubmitAndHideCoroutine(string finalText)
    {
        // 한 프레임 대기 (TMP 내부 업데이트/IME 안정성 보장)
        yield return null;

        // 보장 차원에서 다시 ForceMeshUpdate
        if (inputField != null && inputField.textComponent != null)
            inputField.textComponent.ForceMeshUpdate();

        // ensure actual field contains final text
        if (inputField != null)
            inputField.text = finalText;

        // invoke submit if exists
        if (submitButton != null)
            submitButton.onClick.Invoke();

        // clear and hide
        if (inputField != null)
            inputField.text = "";
        
        EventSystem.current?.SetSelectedGameObject(inputField.gameObject);
        InstantHide();
    }

}
