using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;
using System.Collections;

public class PersonalNotificationManager : NetworkBehaviour
{
    public static PersonalNotificationManager Instance;
    public CanvasGroup canvasGroup;
    public Text personalNotificationText;

    private Coroutine coroutine;

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else Destroy(gameObject);
    }

    [ClientRpc]
    public void ShowPersonalMessageClientRpc(string message, ClientRpcParams clientRpcParams = default)
    {
        if (!IsOwner) return;

        canvasGroup.alpha = 1f;

        // 코루틴 실행 전 반드시 GameObject를 활성화
        if (!canvasGroup.gameObject.activeSelf)
            canvasGroup.gameObject.SetActive(true);

        if (coroutine != null) StopCoroutine(coroutine);
        personalNotificationText.text = message;
        coroutine = StartCoroutine(HideAfterTime(imageMode: false, displaySeconds: 4f, fadeSeconds: 2f));
    }

    public void ShowPersonalMessage(string message)
    {
        if (canvasGroup == null) return;

        personalNotificationText.gameObject.SetActive(true);
        personalNotificationText.text = message;

        // 최상위 캔버스그룹 활성화
        canvasGroup.alpha = 1f;
        if (!canvasGroup.gameObject.activeSelf)
            canvasGroup.gameObject.SetActive(true);

        // 기존 코루틴 정리 후 새로 시작 (텍스트 모드)
        if (coroutine != null) StopCoroutine(coroutine);
        coroutine = StartCoroutine(HideAfterTime(imageMode: false, displaySeconds: 4f, fadeSeconds: 2f));
    }

    public void PersistentShowPersonalMessage(string message)
    {
        if (canvasGroup == null) return;

        // 텍스트만 계속 보이게 함 (코루틴 중지)
        if (coroutine != null) StopCoroutine(coroutine);

        personalNotificationText.gameObject.SetActive(true);
        personalNotificationText.text = message;

        canvasGroup.alpha = 1f;
        if (!canvasGroup.gameObject.activeSelf) canvasGroup.gameObject.SetActive(true);
    }


    private IEnumerator HideAfterTime(bool imageMode, float displaySeconds, float fadeSeconds)
    {
        // 먼저 지정된 시간 동안 유지
        yield return new WaitForSeconds(displaySeconds);

        float t = 0f;

        // 페이드 아웃: alpha 1 -> 0
        while (t < fadeSeconds)
        {
            t += Time.deltaTime;
            float a = Mathf.Lerp(1f, 0f, t / fadeSeconds);

            if (imageMode)
            {
                // 이미지 전용 CanvasGroup이 없으면 전체 canvasGroup을 페이드
                canvasGroup.alpha = a;
            }
            else
            {
                // 텍스트 모드: 전체 캔버스 페이드
                canvasGroup.alpha = a;
            }

            yield return null;
        }

        // 완전 숨김: 텍스트와 이미지 둘 다 안전하게 비활성화
        if (personalNotificationText != null) personalNotificationText.gameObject.SetActive(false);


        // 전체 캔버스도 비활성화
        canvasGroup.alpha = 0f;
        if (canvasGroup.gameObject.activeSelf) canvasGroup.gameObject.SetActive(false);

        coroutine = null;
    }
}
