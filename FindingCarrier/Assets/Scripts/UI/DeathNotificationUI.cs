using UnityEngine;
using System.Collections;
using Unity.Netcode;

public class DeathNotificationUI : NetworkBehaviour
{
    public static DeathNotificationUI Instance { get; private set; }

    // CanvasGroup 컴포넌트를 인스펙터에서 할당합니다.
    [SerializeField]
    private CanvasGroup canvasGroup;

    // 알파 값이 1로 변하는 데 걸리는 시간입니다.
    [SerializeField]
    private float fadeDuration = 0.5f;

    private Coroutine fadeCoroutine;

    private void Awake()
    {
        // 시작 시 UI를 완전히 숨깁니다.
        if (canvasGroup != null)
        {
            canvasGroup.alpha = 0f;
            canvasGroup.interactable = false;
            canvasGroup.blocksRaycasts = false;
        }

        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else Destroy(gameObject);
    }

    public void ShowDeathNotification()
    {
        if (canvasGroup == null) return;

        // 이전에 진행 중이던 코루틴이 있으면 중지합니다.
        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
        }

        // 코루틴을 시작하여 알파 값을 1로 만듭니다.
        fadeCoroutine = StartCoroutine(FadeCanvasGroup(canvasGroup, 1f, fadeDuration));

        // 알림이 표시될 때 UI 상호작용을 활성화합니다.
        canvasGroup.interactable = true;
        canvasGroup.blocksRaycasts = true;
    }

    public void HideDeathNotification()
    {
        if (canvasGroup == null) return;

        // 이전에 진행 중이던 코루틴이 있으면 중지합니다.
        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
        }

        // 코루틴을 시작하여 알파 값을 0으로 만듭니다.
        fadeCoroutine = StartCoroutine(FadeCanvasGroup(canvasGroup, 0f, fadeDuration));

        // 알림이 사라질 때 UI 상호작용을 비활성화합니다.
        canvasGroup.interactable = false;
        canvasGroup.blocksRaycasts = false;
    }

    private IEnumerator FadeCanvasGroup(CanvasGroup cg, float targetAlpha, float duration)
    {
        float startAlpha = cg.alpha;
        float time = 0f;

        while (time < duration)
        {
            cg.alpha = Mathf.Lerp(startAlpha, targetAlpha, time / duration);
            time += Time.deltaTime;
            yield return null;
        }

        cg.alpha = targetAlpha;
        fadeCoroutine = null;
    }
}
