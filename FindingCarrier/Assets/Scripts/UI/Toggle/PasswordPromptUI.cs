using System;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

[DisallowMultipleComponent]
public class PasswordPromptUI : MonoBehaviour
{
    public static PasswordPromptUI Instance { get; private set; }

    [Header("Modal Root (비활성/활성으로 모달 보임)")]
    public GameObject modalRoot;

    [Header("UI Elements")]
    public TMP_Text titleText;               // "비밀번호를 입력하세요" 등
    public TMP_InputField inputField;        // 사용자 입력 필드 (TMP)
    public TMP_Text errorText;               // 경고 메시지 (ex: "비밀번호가 일치하지 않습니다")
    public Button confirmButton;
    public Button closeButton;

    // 내부: 현재 확인 성공 시 실행할 콜백
    private Action onSuccessCallback;
    // 내부: 검증할 정답 비밀번호 (plain text)
    private string expectedPassword = "";

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else Destroy(gameObject);

        // 안전: 버튼 연결 (인스펙터에서 이미 연결되어 있으면 중복 연결 주의)
        if (confirmButton != null) confirmButton.onClick.AddListener(OnConfirmClicked);
        if (closeButton != null) closeButton.onClick.AddListener(OnCloseClicked);

        // 초기 숨김
        if (modalRoot != null) modalRoot.SetActive(false);
        if (errorText != null) errorText.gameObject.SetActive(false);
    }

    private void OnDestroy()
    {
        if (confirmButton != null) confirmButton.onClick.RemoveListener(OnConfirmClicked);
        if (closeButton != null) closeButton.onClick.RemoveListener(OnCloseClicked);
        if (Instance == this) Instance = null;
    }


    public void Show(string title, string expectedPasswordPlain, Action onSuccess)
    {
        if (modalRoot == null) return;

        this.expectedPassword = expectedPasswordPlain ?? "";
        this.onSuccessCallback = onSuccess;

        if (titleText != null) titleText.SetText(title ?? "비밀번호를 입력하세요");
        if (inputField != null) { inputField.text = ""; inputField.ActivateInputField(); }
        if (errorText != null) { errorText.gameObject.SetActive(false); errorText.SetText(""); }

        modalRoot.SetActive(true);
    }

    /// <summary>
    /// 모달 닫기
    /// </summary>
    public void Hide()
    {
        if (modalRoot != null) modalRoot.SetActive(false);
        onSuccessCallback = null;
        expectedPassword = "";
        if (inputField != null) inputField.text = "";
        if (errorText != null) { errorText.gameObject.SetActive(false); errorText.SetText(""); }
    }

    private void OnConfirmClicked()
    {
        // Trim 사용 — 필요하면 케이스 무시 옵션 추가 가능
        string entered = inputField != null ? inputField.text?.Trim() ?? "" : "";

        // 매칭 로직 (단순 평문 비교)
        if (entered == expectedPassword)
        {
            // 성공: 모달 닫고 콜백 호출
            Hide();
            onSuccessCallback?.Invoke();
        }
        else
        {
            // 실패: 경고 표시
            if (errorText != null)
            {
                errorText.gameObject.SetActive(true);
                errorText.SetText("비밀번호가 일치하지 않습니다.");
            }

            // 포커스 재설정 (선택)
            if (inputField != null)
            {
                inputField.ActivateInputField();
            }
        }
    }

    private void OnCloseClicked()
    {
        // 닫기: 모달만 닫음 (별도 동작 없음)
        Hide();
    }
}
