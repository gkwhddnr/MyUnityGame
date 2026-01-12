// ChatUIController.cs (새 파일 또는 기존 파일 교체)
using System.Collections;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

public class ChatUIController : MonoBehaviour
{
    public static ChatUIController Instance;

    [Header("UI Roots")]
    [Tooltip("영구 기록(History)을 저장할 Content (ScrollRect content)")]
    public Transform historyContent;

    [Tooltip("일시적 뷰포트에 표시할 Root (Overlay). 비워두면 historyContent의 복제본 사용)")]
    public Transform viewportContent;

    [Header("Prefab")]
    [Tooltip("메시지 프리팹 (안에 TMP_Text). Rich Text 허용되어야 함.")]
    public GameObject messagePrefab;

    [Header("Timings")]
    public float visibleDuration = 10f; // 화면에 머무르는 시간
    public float fadeDuration = 0.8f;   // 페이드 아웃 시간

    private void Awake()
    {
        if (Instance != null && Instance != this) Destroy(gameObject);
        Instance = this;
    }

    // === 히스토리에 영구 추가 (삭제하지 않음) ===
    public void AddHistoryMessage(string richTextMessage)
    {
        if (string.IsNullOrEmpty(richTextMessage) || messagePrefab == null || historyContent == null) return;

        var go = Instantiate(messagePrefab, historyContent);
        go.SetActive(true);
        var text = go.GetComponentInChildren<TMP_Text>(true);
        if (text != null)
        {
            text.richText = true;
            text.text = richTextMessage;
        }

        if (go.GetComponent<CanvasGroup>() == null)
            go.AddComponent<CanvasGroup>();

        // 스크롤 맨 아래로
        Canvas.ForceUpdateCanvases();
        var sr = historyContent.GetComponentInParent<ScrollRect>();
        if (sr != null) sr.verticalNormalizedPosition = 0f;
    }

    // === 뷰포트에 일시적 표시 (히스토리에는 영향 없음) ===
    public void ShowViewportMessage(string richTextMessage)
    {
        if (string.IsNullOrEmpty(richTextMessage) || messagePrefab == null) return;

        Transform parent = viewportContent ?? historyContent;
        if (parent == null) return;

        var go = Instantiate(messagePrefab, parent);
        go.SetActive(true);
        var text = go.GetComponentInChildren<TMP_Text>(true);
        if (text != null)
        {
            text.richText = true;
            text.text = richTextMessage;
        }

        // 페이드 제어용 CanvasGroup
        CanvasGroup cg = go.GetComponent<CanvasGroup>();
        if (cg == null) cg = go.AddComponent<CanvasGroup>();
        cg.alpha = 1f;

        // 뷰포트 전용이면 위치/레이아웃 조정이 필요할 수 있음.
        StartCoroutine(TemporaryViewportRoutine(go, cg));
    }

    private IEnumerator TemporaryViewportRoutine(GameObject go, CanvasGroup cg)
    {
        if (visibleDuration > 0f)
            yield return new WaitForSeconds(visibleDuration);

        float t = 0f;
        float dur = Mathf.Max(0.0001f, fadeDuration);
        float start = cg.alpha;
        while (t < dur)
        {
            t += Time.deltaTime;
            cg.alpha = Mathf.Lerp(start, 0f, t / dur);
            yield return null;
        }
        if (go != null) Destroy(go);
    }
}
