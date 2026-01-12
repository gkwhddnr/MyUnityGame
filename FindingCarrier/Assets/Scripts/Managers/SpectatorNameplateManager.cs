using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

public class SpectatorNameplateManager : MonoBehaviour
{
    public static SpectatorNameplateManager Instance { get; private set; }

    [Header("Overlay Canvas (optional)")]
    [Tooltip("만약 지정하지 않으면 런타임에 ScreenSpace-Overlay Canvas를 생성합니다.")]
    public Canvas overlayCanvas;

    [Header("Overlay Text Settings")]
    public TMP_FontAsset fontAsset;         // Inspector에서 할당 권장 (없는 경우 TMP 기본 사용)
    public int fontSize = 24;
    public Vector2 screenOffset = new Vector2(0, 40); // 월드 포인트에서 화면상 오프셋

    [Header("Update")]
    [Tooltip("오버레이 위치를 매 프레임 업데이트 (보통 true).")]
    public bool updateEveryFrame = true;

    // 내부 매핑: player OwnerClientId -> overlay GameObject (RectTransform + TextMeshProUGUI)
    private readonly Dictionary<ulong, GameObject> overlays = new Dictionary<ulong, GameObject>();

    // 월드 텍스트를 같이 켜고 싶을 때 대비하여 원래 월드 nameText 상태를 잠시 저장
    private readonly Dictionary<TMP_Text, bool> worldNameOriginalState = new Dictionary<TMP_Text, bool>();

    // 자동 갱신 주기 (플레이어 수가 변하는 경우를 위해)
    private float nextRefreshTime = 0f;
    private float refreshInterval = 1f;

    private Camera mainCam;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        mainCam = Camera.main;

        // Canvas가 에디터에서 지정되어 있지 않으면 런타임에 생성
        if (overlayCanvas == null)
        {
            CreateOverlayCanvas();
        }
        else
        {
            // Make sure canvas is overlay or ScreenSpaceCamera (set top)
            overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        }

        // 폰트가 비어있으면 TMP 기본 폰트 사용 (유효성 문제는 콘솔에 출력)
        if (fontAsset == null)
        {
            // optional: leave null (TextMeshPro will use default)
        }
    }

    private void Update()
    {
        if (updateEveryFrame) UpdateOverlays();

        // 정기 갱신: 플레이어 추가/제거 대응
        if (Time.time >= nextRefreshTime)
        {
            nextRefreshTime = Time.time + refreshInterval;
            RefreshOverlaysIfNeeded();
        }
    }

    private void CreateOverlayCanvas()
    {
        var go = new GameObject("SpectatorNameplateCanvas");
        go.transform.SetParent(transform, worldPositionStays: false);
        overlayCanvas = go.AddComponent<Canvas>();
        overlayCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
        overlayCanvas.overrideSorting = true;
        overlayCanvas.sortingOrder = 10000;
        go.AddComponent<CanvasScaler>();
        go.AddComponent<GraphicRaycaster>();
    }

    /// <summary>
    /// 모든 플레이어의 nameplate 오버레이를 보이거나 숨깁니다.
    /// showWorldNameTexts를 true로 하면 플레이어 프리팹 내부의 world TMP도 활성화합니다.
    /// (보통 관전용으로만 사용: 누가 죽었을 때 호출하세요.)
    /// </summary>
    public void ShowAllNameplates(bool show, bool showWorldNameTexts = false)
    {
        EnsureMainCamera();

        // players 찾기
        var players = GetAllPlayerMovementInstances().ToArray();

        // 1) 월드 nameText 원상태 저장/복원 또는 강제 설정
        if (showWorldNameTexts)
        {
            foreach (var pm in players)
            {
                if (pm == null) continue;
                var t = pm.nameText;
                if (t == null) continue;
                if (!worldNameOriginalState.ContainsKey(t))
                    worldNameOriginalState[t] = t.gameObject.activeSelf;
                t.gameObject.SetActive(true);
            }
        }
        else
        {
            // hide/restore: if turning off, restore original states
            if (!show)
            {
                foreach (var kv in worldNameOriginalState)
                {
                    if (kv.Key != null)
                        kv.Key.gameObject.SetActive(kv.Value);
                }
                worldNameOriginalState.Clear();
            }
        }

        // 2) 오버레이 생성 / 제거
        if (show)
        {
            foreach (var pm in players)
            {
                if (pm == null) continue;
                ulong owner = GetOwnerFromPlayerMovement(pm);
                if (owner == 0) continue;

                if (!overlays.ContainsKey(owner))
                {
                    CreateOverlayFor(pm, owner);
                }
            }
        }
        else
        {
            // 제거
            foreach (var kv in overlays.ToArray())
            {
                if (kv.Value != null) Destroy(kv.Value);
                overlays.Remove(kv.Key);
            }
        }
    }

    /// <summary>
    /// 잠깐(초단위)만 모든 nameplate를 보여주고 자동으로 숨깁니다.
    /// </summary>
    public void ShowAllNameplatesForSeconds(float seconds, bool showWorldNameTexts = false)
    {
        ShowAllNameplates(true, showWorldNameTexts);
        StopAllCoroutines();
        StartCoroutine(DelayedHide(seconds));
    }

    private IEnumerator DelayedHide(float seconds)
    {
        yield return new WaitForSeconds(seconds);
        ShowAllNameplates(false);
    }

    /// <summary>
    /// 매 프레임 위치 업데이트
    /// </summary>
    public void UpdateOverlays()
    {
        EnsureMainCamera();
        if (overlayCanvas == null) return;

        var canvasRect = overlayCanvas.transform as RectTransform;
        bool isOverlay = overlayCanvas.renderMode == RenderMode.ScreenSpaceOverlay;
        Camera cam = overlayCanvas.worldCamera;
        if (!isOverlay && cam == null) cam = mainCam;

        var toRemove = new List<ulong>();

        foreach (var kv in overlays)
        {
            ulong owner = kv.Key;
            GameObject overlayGO = kv.Value;
            if (overlayGO == null)
            {
                toRemove.Add(owner);
                continue;
            }

            // find associated PlayerMovement for owner
            var pm = FindPlayerMovementByOwner(owner);
            if (pm == null)
            {
                // 플레이어 오브젝트가 사라졌으면 이름을 여전히 남기고 싶다면 (원하시면) 여기서는 삭제합니다.
                // 대신 보관하려면 여기서 스킵.
                // toRemove.Add(owner);
                // continue;

                // 만약 플레이어 오브젝트가 파괴되어도 이름을 계속 보여주고 싶다면
                // overlay의 텍스트는 보존되고 위치는 고정할 수 있습니다. 여기서는 삭제하지 않습니다.
            }

            Vector3 worldPos;
            if (pm != null)
                worldPos = pm.transform.position + Vector3.up * 1.8f; // 머리 위
            else
                worldPos = overlayGO.transform.position; // fallback

            Vector3 screenPoint = mainCam.WorldToScreenPoint(worldPos);

            // behind camera -> hide overlay
            bool behind = screenPoint.z < 0f;
            overlayGO.SetActive(!behind);

            if (!behind)
            {
                RectTransform rt = overlayGO.GetComponent<RectTransform>();
                Vector2 localPoint;
                if (RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screenPoint + (Vector3)screenOffset, cam, out localPoint))
                {
                    rt.anchoredPosition = localPoint;
                }
            }
        }

        // cleanup
        foreach (var k in toRemove) overlays.Remove(k);
    }

    private void EnsureMainCamera()
    {
        if (mainCam == null) mainCam = Camera.main;
    }

    private void RefreshOverlaysIfNeeded()
    {
        // 씬의 플레이어 수가 overlays와 다르면 갱신 (생성/제거)
        var pms = GetAllPlayerMovementInstances().ToArray();
        var ownersInScene = new HashSet<ulong>(pms.Select(pm => GetOwnerFromPlayerMovement(pm)).Where(id => id != 0));

        // create missing
        foreach (var pm in pms)
        {
            if (pm == null) continue;
            ulong owner = GetOwnerFromPlayerMovement(pm);
            if (owner == 0) continue;
            if (!overlays.ContainsKey(owner))
            {
                CreateOverlayFor(pm, owner);
            }
        }

        // remove overlays whose owners no longer exist
        var toRemove = overlays.Keys.Where(k => !ownersInScene.Contains(k)).ToArray();
        foreach (var k in toRemove)
        {
            if (overlays[k] != null) Destroy(overlays[k]);
            overlays.Remove(k);
        }
    }

    private void CreateOverlayFor(PlayerMovement pm, ulong ownerClientId)
    {
        if (overlayCanvas == null) CreateOverlayCanvas();
        if (pm == null) return;

        // 오버레이 루트
        GameObject go = new GameObject($"NameOverlay_{ownerClientId}");
        go.transform.SetParent(overlayCanvas.transform, worldPositionStays: false);

        var rt = go.AddComponent<RectTransform>();
        rt.sizeDelta = new Vector2(400, 40);
        rt.pivot = new Vector2(0.5f, 0);

        var textGO = new GameObject("NameText");
        textGO.transform.SetParent(go.transform, worldPositionStays: false);
        var txtRt = textGO.AddComponent<RectTransform>();
        txtRt.anchorMin = new Vector2(0, 0);
        txtRt.anchorMax = new Vector2(1, 1);
        txtRt.offsetMin = Vector2.zero;
        txtRt.offsetMax = Vector2.zero;

        var tmp = textGO.AddComponent<TextMeshProUGUI>();
        if (fontAsset != null) tmp.font = fontAsset;
        tmp.fontSize = fontSize;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.richText = true;

        // 텍스트 내용: 플레이어의 네트워크 네임 또는 Owner 기반 fallback
        string nameStr = pm.playerName != null ? pm.playerName.Value.ToString() : $"Player{ownerClientId}";
        tmp.text = GetColoredNameBySlot(ownerClientId, nameStr);

        overlays[ownerClientId] = go;

        // 즉시 위치 업데이트 한 번
        UpdateOverlays();
    }

    private string GetColoredNameBySlot(ulong ownerClientId, string rawName)
    {
        // 슬롯 기반 색상(기존 규칙과 동일)
        var psMgr = FindFirstObjectByType<PlayerSpectatorManager>();
        int slotNum = 0;
        if (psMgr != null)
        {
            var list = psMgr.playerSlots.Value.PlayerSlots;
            if (list != null)
            {
                var slot = list.Find(s => s.ClientId == ownerClientId);
                if (slot.SlotNumber != 0) slotNum = slot.SlotNumber;
            }
        }

        string hex = null;
        switch (slotNum)
        {
            case 1: hex = "#FF0000"; break;
            case 2: hex = "#0000FF"; break;
            case 3: hex = "#90EE90"; break;
            case 4: hex = "#800080"; break;
            case 5: hex = "#FFA500"; break;
            case 6: hex = "#8B4513"; break;
            case 7: hex = "#FFFFFF"; break;
            case 8: hex = "#FFFF00"; break;
            default: hex = null; break;
        }

        if (!string.IsNullOrEmpty(hex))
            return $"<color={hex}>{EscapeRichText(rawName)}</color>";
        return EscapeRichText(rawName);
    }

    private string EscapeRichText(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;
        return input.Replace("<", "&lt;").Replace(">", "&gt;");
    }

    private PlayerMovement FindPlayerMovementByOwner(ulong owner)
    {
#if UNITY_2023_2_OR_NEWER
        var all = UnityEngine.Object.FindObjectsByType<PlayerMovement>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        var all = Resources.FindObjectsOfTypeAll<PlayerMovement>();
#endif
        foreach (var pm in all)
        {
            if (pm == null) continue;
            if (!pm.gameObject.scene.IsValid()) continue;
            if (GetOwnerFromPlayerMovement(pm) == owner) return pm;
        }
        return null;
    }

    private ulong GetOwnerFromPlayerMovement(PlayerMovement pm)
    {
        if (pm == null) return 0;
        try
        {
            // NetworkBehaviour의 OwnerClientId 접근
            return pm.OwnerClientId;
        }
        catch
        {
            return 0;
        }
    }

    private IEnumerable<PlayerMovement> GetAllPlayerMovementInstances()
    {
#if UNITY_2023_2_OR_NEWER
        return UnityEngine.Object.FindObjectsByType<PlayerMovement>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                                 .Where(pm => pm != null && pm.gameObject.scene.IsValid());
#else
        var all = Resources.FindObjectsOfTypeAll<PlayerMovement>();
        foreach (var pm in all)
        {
            if (pm == null) continue;
            if (!pm.gameObject.scene.IsValid() || !pm.gameObject.scene.isLoaded) continue;
            yield return pm;
        }
#endif
    }

    private void OnDestroy()
    {
        // 정리
        foreach (var kv in overlays)
            if (kv.Value != null) Destroy(kv.Value);
        overlays.Clear();
    }
}
