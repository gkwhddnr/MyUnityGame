using System.Collections;
using UnityEngine;
using Unity.Netcode;

[DisallowMultipleComponent]
public class CanvasGroupOnLocalPlayerSpawn : MonoBehaviour
{
    public CanvasGroup targetCanvasGroup;

    private bool fadeIn = false;
    private float fadeDuration = 3f;
    public float inspectorFromAlpha = 0f;
    public float inspectorToAlpha = 1f;

    // 내부 상태
    private Coroutine fadeCoroutine;

    private void Reset()
    {
        // 편의: 컴포넌트에 바로 붙여두었다면 자동 참조
        if (targetCanvasGroup == null)
            targetCanvasGroup = GetComponent<CanvasGroup>();
    }

    private void OnEnable()
    {
        // 기본값 안전성
        if (targetCanvasGroup == null) return;

        // 초기 상태: 숨김 (요구는 '플레이어 생성 후 1' 이므로 시작은 0)
        HideCanvasGroupImmediate();
        // 시작 코루틴으로 로컬 플레이어가 생길 때까지 대기
        StartCoroutine(WaitForLocalPlayerAndActivate());

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
        }
    }

    private void OnDisable()
    {
        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
            fadeCoroutine = null;
        }

        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
        }
    }

    private IEnumerator WaitForLocalPlayerAndActivate()
    {
        // 1) NetworkManager가 생성될 때까지 대기
        while (NetworkManager.Singleton == null)
            yield return null;

        // 2) Netcode가 클라이언트/호스트로 준비될 때까지 기다림
        //    (호스트/서버를 수동으로 시작할 경우를 대비)
        while (NetworkManager.Singleton.SpawnManager == null)
            yield return null;

        // 3) 로컬 플레이어 오브젝트가 spawn 될 때까지 대기
        //    SpawnManager.GetLocalPlayerObject() 가 non-null 이 되는 시점이 "로컬 플레이어 생성" 시점
        while (NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject() == null)
            yield return null;

        NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
        NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;


        // 로컬 플레이어 생성 완료 ? 이제 CanvasGroup 노출
        ActivateCanvasGroup();
    }

    private void ActivateCanvasGroup()
    {
        if (targetCanvasGroup == null) return;

        // 상호작용 가능하게 설정 (곧 alpha를 올릴 예정)
        targetCanvasGroup.interactable = true;
        targetCanvasGroup.blocksRaycasts = true;

        if (fadeIn)
        {
            if (fadeCoroutine != null) StopCoroutine(fadeCoroutine);
            fadeCoroutine = StartCoroutine(FadeCanvasGroup(targetCanvasGroup, targetCanvasGroup.alpha, 1f, fadeDuration));
        }
        else
        {
            // 즉시 보이기
            targetCanvasGroup.alpha = 1f;
        }
    }

    private void HideCanvasGroupImmediate()
    {
        if (targetCanvasGroup == null) return;

        targetCanvasGroup.alpha = 0f;
        targetCanvasGroup.interactable = false;
        targetCanvasGroup.blocksRaycasts = false;
    }

    public IEnumerator FadeCanvasGroup(CanvasGroup cg, float from, float to, float duration)
    {
        if (cg == null)
        {
            fadeCoroutine = null;
            yield break;
        }

        float t = 0f;
        // 안전: duration이 0이면 즉시 설정
        if (duration <= 0f)
        {
            cg.alpha = to;
            fadeCoroutine = null;
            yield break;
        }

        while (t < duration)
        {
            t += Time.unscaledDeltaTime;
            cg.alpha = Mathf.Lerp(from, to, Mathf.Clamp01(t / duration));
            yield return null;
        }

        cg.alpha = to;
        fadeCoroutine = null;
    }

    public void FadeCanvasGroup2(CanvasGroup cg, float from, float to, float duration)
    {
        if (cg == null) return;

        // 멈춰야 하는 기존 코루틴이 있다면 정리
        if (fadeCoroutine != null)
        {
            StopCoroutine(fadeCoroutine);
            fadeCoroutine = null;
        }

        // 시작 전에 즉시 from 값 세팅 (원하시면 주석 처리 가능)
        cg.alpha = from;

        // 코루틴 시작
        fadeCoroutine = StartCoroutine(FadeCanvasGroup(cg, from, to, duration));
    }

    private void OnClientDisconnect(ulong clientId)
    {
        if (NetworkManager.Singleton == null) return;

        if (clientId == NetworkManager.Singleton.LocalClientId)
        {
            HideCanvasGroupImmediate();
        }
    }

    public void FadeCanvasGroup2_FromInspectorValues()
    {
        // 우선 inspectorTargetCanvasGroup이 비어있으면 클래스의 targetCanvasGroup 사용
        FadeCanvasGroup2(targetCanvasGroup, 0f, 1f, fadeDuration);
    }
}
