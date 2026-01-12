// CanvasGroupToggle.cs
using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;
using UnityEngine.UI;

public class CanvasGroupToggle : MonoBehaviour
{
    [SerializeField] private CanvasGroup targetGroup; // 적용할 CanvasGroup
    [SerializeField] private KeyCode toggleKey = KeyCode.Tab; // 토글할 키 (기본 Tab)
    public AudioSource audioSource;

    private void Update()
    {
        if (Input.GetKeyDown(toggleKey))
        {
            // 채팅중이면 토글하지 않음
            if (IsTypingOnInput()) return;

            ToggleCanvasGroup();

            if (audioSource != null)
                audioSource.Play();
        }
    }

    private bool IsTypingOnInput()
    {
        // EventSystem에서 현재 선택된 오브젝트가 InputField/TMP_InputField인지 확인
        if (EventSystem.current == null) return false;
        var go = EventSystem.current.currentSelectedGameObject;
        if (go == null) return false;

        // TMP
        if (go.GetComponent<TMP_InputField>() != null) return true;
        if (go.GetComponentInParent<TMP_InputField>() != null) return true;

        // Legacy InputField (혹시 사용하는 경우)
        if (go.GetComponent<InputField>() != null) return true;
        if (go.GetComponentInParent<InputField>() != null) return true;

        return false;
    }

    private void ToggleCanvasGroup()
    {
        if (targetGroup == null) return;

        // Alpha 0이면 1로, 1이면 0으로 토글
        targetGroup.alpha = targetGroup.alpha > 0 ? 0 : 1;

        // 필요하다면 RaycastBlock, Interactable도 같이 변경
        targetGroup.interactable = targetGroup.alpha > 0;
        targetGroup.blocksRaycasts = targetGroup.alpha > 0;
    }
}
