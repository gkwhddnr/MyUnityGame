using UnityEngine;
using System.Collections;

public class DayNightCanvasFader : MonoBehaviour
{

    public CanvasGroup canvasGroup;
    public DayNightManager dayNightManager;
    public float fadeDuration = 1f;

    private Coroutine fadeCoroutine;

    // DayNightManager의 isNight 값이 변경될 때 호출되는 이벤트 핸들러입니다.
    private void OnIsNightChanged(bool oldValue, bool newValue)
    {
        // 현재 실행 중인 코루틴이 있다면 중단합니다.
        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
        }

        // 새로운 페이드 코루틴을 시작합니다.
        float targetAlpha = newValue ? 0f : 1f;
        fadeCoroutine = StartCoroutine(DoFade(targetAlpha));
    }

    private void Start()
    {
        if (dayNightManager == null) return;

        // isNight 값 변경 이벤트에 OnIsNightChanged 함수를 등록합니다.
        dayNightManager.isNight.OnValueChanged += OnIsNightChanged;

        // 초기 상태를 설정합니다.
        // OnNetworkSpawn 대신 Start에서 초기값을 설정합니다.
        canvasGroup.alpha = dayNightManager.isNight.Value ? 1f : 0f;
    }

    private void OnDestroy()
    {
        if (dayNightManager != null)
        {
            // 스크립트가 파괴될 때 리스너를 해제합니다.
            dayNightManager.isNight.OnValueChanged -= OnIsNightChanged;
        }
    }
    private IEnumerator DoFade(float targetAlpha)
    {
        float startAlpha = canvasGroup.alpha;
        float elapsedTime = 0f;

        while (elapsedTime < fadeDuration)
        {
            elapsedTime += Time.deltaTime;
            canvasGroup.alpha = Mathf.Lerp(startAlpha, targetAlpha, elapsedTime / fadeDuration);
            yield return null;
        }

        // 페이드가 완료된 후 최종 값으로 설정하여 정확도를 보장합니다.
        canvasGroup.alpha = targetAlpha;
        fadeCoroutine = null;
    }
}
