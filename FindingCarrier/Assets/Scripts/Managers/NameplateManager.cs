// NameplateManager.cs
using System.Collections.Generic;
using TMPro;
using UnityEngine;

public class NameplateManager : MonoBehaviour
{
    public static NameplateManager Instance { get; private set; }

    [Header("Assign")]
    public Canvas nameplateCanvas; // Screen Space - Overlay 권장
    public GameObject nameplatePrefab; // 루트가 RectTransform이고 안에 TMP_Text (TextMeshProUGUI)

    [Header("Settings")]
    public Vector3 worldOffset = new Vector3(0, 2.0f, 0); // 머리 위치 오프셋
    public bool disableSceneRootIfFound = true; // 자동으로 씬의 고정 루트 비활성화

    private Dictionary<ulong, GameObject> created = new Dictionary<ulong, GameObject>();

    private readonly string[] slotHex = new string[] {
        null, // index 0 unused
        "#FF0000", "#0000FF", "#90EE90", "#800080", "#FFA500", "#8B4513", "#FFFFFF", "#FFFF00"
    };

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        if (nameplateCanvas == null)
        {
            // 자동 생성 (Screen Space - Overlay Canvas)
            var go = new GameObject("NameplateCanvas");
            nameplateCanvas = go.AddComponent<Canvas>();
            nameplateCanvas.renderMode = RenderMode.ScreenSpaceOverlay;
            go.AddComponent<UnityEngine.UI.CanvasScaler>();
            go.AddComponent<UnityEngine.UI.GraphicRaycaster>();
            DontDestroyOnLoad(go);
        }

        if (disableSceneRootIfFound)
        {
            var root = GameObject.Find("NAMEplayteprefabroot"); // 기존 고정 이름이 이거면 비활성
            if (root != null) root.SetActive(false);
        }
    }

    // 호출: PlayerMovement.OnNetworkSpawn() (캐릭터 인스턴스에서)
    public void RegisterPlayer(ulong ownerClientId, Transform followTarget, string displayName, int slot = 0)
    {
        if (followTarget == null || string.IsNullOrEmpty(displayName)) return;
        if (created.ContainsKey(ownerClientId)) return;

        if (nameplatePrefab == null)
        {
            Debug.LogWarning("[NameplateManager] nameplatePrefab is not assigned!");
            return;
        }

        var go = Instantiate(nameplatePrefab, nameplateCanvas.transform, false);
        go.name = $"Nameplate_{ownerClientId}";
        var controller = go.GetComponent<NameplateController>();
        if (controller == null)
        {
            controller = go.AddComponent<NameplateController>();
        }

        string colored = displayName;
        if (slot >= 1 && slot <= 8)
        {
            var hex = slotHex[slot];
            if (!string.IsNullOrEmpty(hex)) colored = $"<color={hex}>{EscapeRichText(displayName)}</color>";
        }
        controller.Initialize(followTarget, nameplateCanvas.GetComponent<RectTransform>(), worldOffset, colored);

        created[ownerClientId] = go;
    }

    public void UpdatePlayerName(ulong ownerClientId, string displayName, int slot = 0)
    {
        if (!created.TryGetValue(ownerClientId, out var go)) return;
        var controller = go.GetComponent<NameplateController>();
        if (controller == null) return;

        string colored = displayName;
        if (slot >= 1 && slot <= 8)
        {
            var hex = slotHex[slot];
            if (!string.IsNullOrEmpty(hex)) colored = $"<color={hex}>{EscapeRichText(displayName)}</color>";
        }
        controller.SetText(colored);
    }

    public void UnregisterPlayer(ulong ownerClientId)
    {
        if (!created.TryGetValue(ownerClientId, out var go)) return;
        created.Remove(ownerClientId);
        if (go != null) Destroy(go);
    }

    private string EscapeRichText(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
