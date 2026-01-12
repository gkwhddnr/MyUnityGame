// ChatEnterSubmit.cs
using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using TMPro;

public class ChatEnterSubmit : MonoBehaviour
{
    [Tooltip("입력필드 (옵션). 없으면 씬에서 활성화된 TMP_InputField 를 시도하여 찾습니다.")]
    public TMP_InputField inputField;

    [Tooltip("Message Submit 버튼 (옵션). 없으면 이름으로 찾아봅니다: 'Message Submit'")]
    public Button submitButton;

    void Start()
    {
        // fallback: 필드가 지정되지 않았다면 자동으로 찾아본다 (멀티플레이어 위젯 구조에 따라 실패할 수 있음)
        if (inputField == null)
        {
            inputField = FindFirstObjectByType<TMP_InputField>();
        }

        if (submitButton == null)
        {
            // 이름이 정확하다면 찾아오기 시도
            var go = GameObject.Find("Message Submit");
            if (go != null) submitButton = go.GetComponent<Button>();
        }
    }

    void Update()
    {
        // IME 조합중이면 무시
        if (!string.IsNullOrEmpty(Input.compositionString)) return;

        if (Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.KeypadEnter))
        {
            // 입력필드에 포커스가 있을 때만 처리
            if (inputField != null && inputField.isFocused)
            {
                // 텍스트(공백 제외)
                string txt = (inputField.text ?? "").Trim();
                if (string.IsNullOrEmpty(txt))
                {
                    // 비어있으면 그냥 포커스 해제 후 닫는 로직(원하면) - 여기서는 포커스 해제만
                    EventSystem.current?.SetSelectedGameObject(null);
                    return;
                }

                // 1) 우선 버튼이 있으면 클릭 호출
                if (submitButton != null)
                {
                    submitButton.onClick.Invoke();
                }
                else
                {
                    // 2) fallback: ChatInputHandler 가 있으면 직접 호출
                    var handler = FindFirstObjectByType<ChatInputHandler>();
                    if (handler != null)
                    {
                        handler.TrySubmitFromInput();
                    }
                }

                // 짧은 지연 후에도 남아있으면 강제로 처리 (TMP 타이밍 문제 방지)
                StartCoroutine(EnsureSubmitAndClearOneFrame());
            }
        }
    }

    private IEnumerator EnsureSubmitAndClearOneFrame()
    {
        yield return null; // 한 프레임 대기

        if (inputField == null)
            inputField = FindFirstObjectByType<TMP_InputField>();

        if (inputField != null)
        {
            string remaining = (inputField.text ?? "").Trim();
            if (!string.IsNullOrEmpty(remaining))
            {
                // 다시 시도: ChatInputHandler 또는 버튼 재시도
                var handler = FindFirstObjectByType<ChatInputHandler>();
                if (handler != null)
                {
                    handler.TrySubmitFromInput();
                }
                else if (submitButton != null)
                {
                    submitButton.onClick.Invoke();
                }
            }

            // 포커스 해제해서 다른 키 (토글 등) 사용할 수 있게 함
            EventSystem.current?.SetSelectedGameObject(null);
        }
    }
}
