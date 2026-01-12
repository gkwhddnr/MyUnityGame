using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class PasswordVisibilityHelper : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private TMP_InputField passwordInput; // 비밀번호 입력창
    [SerializeField] private Toggle toggle; // 토글 UI

    private void Awake()
    {
        if (toggle != null)
            toggle.onValueChanged.AddListener(OnToggleValueChanged);
    }

    private void OnDestroy()
    {
        if (toggle != null)
            toggle.onValueChanged.RemoveListener(OnToggleValueChanged);
    }

    private void OnToggleValueChanged(bool isOn)
    {
        if (passwordInput == null) return;

        if (isOn)
            passwordInput.contentType = TMP_InputField.ContentType.Standard; // 평문 표시
        else
            passwordInput.contentType = TMP_InputField.ContentType.Password; // 비밀번호 모드

        passwordInput.ForceLabelUpdate(); // 즉시 반영
    }
}
