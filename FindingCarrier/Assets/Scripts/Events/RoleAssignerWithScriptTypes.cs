using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Netcode;
using Unity.Collections;


#if UNITY_EDITOR
using UnityEditor;
#endif

public class RoleAssignerWithScriptTypes : NetworkBehaviour
{
    [Serializable]
    public class RoleEntry
    {
        public Sprite roleSprite;
        public string roleName;

        [Tooltip("런타임에 사용할 컴포넌트 타입 이름. 에디터에서 roleScript를 선택하면 자동으로 채워집니다.")]
        public string componentTypeName;

#if UNITY_EDITOR
        [Header("Editor only")]
        public MonoScript roleScript;

        public void EditorFillTypeName()
        {
            if (roleScript == null)
                return;
            var klass = roleScript.GetClass();
            if (klass != null)
            {
                componentTypeName = klass.AssemblyQualifiedName ?? klass.FullName;
            }
        }
#endif
    }

    [Serializable]
    public class MandatoryRoleSpec
    {
        [Tooltip("Inspector에서 반드시 할당할 MonoScript (해당 스크립트 타입과 매칭되는 RoleEntry가 있어야 합니다).")]
        public MonoScript mandatoryRoleScript;

        // 런타임에 비교할 타입명 (에디터에서 자동 채움)
        public string componentTypeName;

#if UNITY_EDITOR
        public void EditorFillTypeName()
        {
            if (mandatoryRoleScript == null) return;
            var klass = mandatoryRoleScript.GetClass();
            if (klass != null)
            {
                componentTypeName = klass.AssemblyQualifiedName ?? klass.FullName;
            }
        }
#endif
    }

    [Header("Roles (each RoleEntry must represent one role)")]
    public RoleEntry[] roleEntries;

    [Header("Mandatory roles (assign scripts here in inspector)")]
    [Tooltip("인스펙터에서 MonoScript를 할당하세요. 해당 스크립트와 매칭되는 RoleEntry가 있어야 서버가 강제로 포함합니다.")]
    public MandatoryRoleSpec[] mandatoryRoles;

    [Header("Client UI")]
    public Image blackScreenImage;       // full-screen black image (Canvas)
    public CanvasGroup roleCanvasGroup;  // role display group (image + text)
    public Image roleDisplayImage;
    public TMP_Text roleDisplayText;

    [Header("Timings")]
    public float fadeDuration = 0.8f;
    public float showDuration = 2.5f;
    [Range(0f, 1f)]
    public float darkenTargetAlpha = 0.6f;

    [Header("Misc")]
    public bool debugLog = true;

    // 서버에서 보관 (선택적)
    private readonly Dictionary<ulong, string> assignedRoles = new Dictionary<ulong, string>();

    private void Start()
    {
        // 클라이언트 UI 초기화
        if (!IsServer)
        {
            if (roleCanvasGroup != null)
            {
                roleCanvasGroup.alpha = 0f;
                roleCanvasGroup.interactable = false;
                roleCanvasGroup.blocksRaycasts = false;
                roleCanvasGroup.gameObject.SetActive(true);
            }
            if (blackScreenImage != null)
            {
                var c = blackScreenImage.color;
                c.a = 0f;
                blackScreenImage.color = c;
                blackScreenImage.gameObject.SetActive(true);
                blackScreenImage.raycastTarget = false;
            }
            if (roleDisplayImage != null) roleDisplayImage.gameObject.SetActive(true);
            if (roleDisplayText != null) roleDisplayText.gameObject.SetActive(true);
        }
    }

    // 버튼에 연결 (클라이언트가 누르면 ServerRpc로 요청)
    public void OnGameStartButtonPressed()
    {
        if (IsServer)
        {
            StartAssignmentOnServer();
        }
        else
        {
            RequestAssignmentServerRpc();
        }
    }

    [ServerRpc(RequireOwnership = false)]
    private void RequestAssignmentServerRpc(ServerRpcParams rpcParams = default)
    {
        if (!IsServer) return;
        StartAssignmentOnServer();
    }

    // ---------- Server side ----------
    private void StartAssignmentOnServer()
    {
        if (!IsServer) return;

        if (roleEntries == null || roleEntries.Length == 0)
        {
            Debug.LogError("[RoleAssigner] No role entries defined.");
            return;
        }

        var clients = NetworkManager.Singleton.ConnectedClientsList;
        if (clients == null || clients.Count == 0)
        {
            Debug.LogWarning("[RoleAssigner] No connected players.");
            return;
        }

        // Build list of (clientId, characterGameObject)
        var playerObjects = new List<(ulong clientId, GameObject go)>();
        foreach (var c in clients)
        {
            GameObject playerGo = null;

            // Prefer PlayerHealth or PlayerMovement.IsCharacterInstance() objects
            if (NetworkManager.Singleton != null)
            {
                foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
                {
                    var netObj = kv.Value;
                    if (netObj == null) continue;
                    if (netObj.OwnerClientId != c.ClientId) continue;

                    if (netObj.GetComponent<PlayerHealth>() != null)
                    {
                        playerGo = netObj.gameObject;
                        break;
                    }
                    var pm = netObj.GetComponent<PlayerMovement>();
                    if (pm != null && pm.IsCharacterInstance())
                    {
                        playerGo = netObj.gameObject;
                        break;
                    }

                    // last resort: pick first spawned owned object
                    if (playerGo == null) playerGo = netObj.gameObject;
                }
            }

            if (playerGo != null)
            {
                playerObjects.Add((c.ClientId, playerGo));
            }
            else
            {
                if (debugLog) Debug.LogWarning($"[RoleAssigner] Could not find a spawned object for client {c.ClientId}. Skipping.");
            }
        }

        if (playerObjects.Count == 0)
        {
            Debug.LogWarning("[RoleAssigner] Could not locate player GameObjects.");
            return;
        }

        int playerCount = playerObjects.Count;

        // Prepare shuffled role indices (no duplication)
        var indices = Enumerable.Range(0, roleEntries.Length).ToList();
        Shuffle(indices);

        // Build list of mandatory indices from mandatoryRoles (match by componentTypeName to roleEntries)
        var mandatoryIndices = new List<int>();
        if (mandatoryRoles != null && mandatoryRoles.Length > 0)
        {
            foreach (var m in mandatoryRoles)
            {
                if (m == null)
                {
                    Debug.LogWarning("[RoleAssigner] Found null mandatory role spec, skipping.");
                    continue;
                }

                // If componentTypeName not filled (editor), try to fill from MonoScript
                string mandatoryType = m.componentTypeName;
                if (string.IsNullOrEmpty(mandatoryType) && m.mandatoryRoleScript != null)
                {
                    var klass = m.mandatoryRoleScript.GetClass();
                    if (klass != null)
                        mandatoryType = klass.AssemblyQualifiedName ?? klass.FullName;
                }

                if (string.IsNullOrEmpty(mandatoryType))
                {
                    Debug.LogError("[RoleAssigner] Mandatory role spec missing script/type. Please assign a MonoScript in the inspector.");
                    continue;
                }

                // Find matching roleEntries index by componentTypeName
                int foundIdx = -1;
                for (int i = 0; i < roleEntries.Length; i++)
                {
                    var e = roleEntries[i];
                    if (e == null) continue;
                    if (string.IsNullOrEmpty(e.componentTypeName)) continue;

                    // Compare assembly-qualified or plain names
                    if (string.Equals(e.componentTypeName, mandatoryType, StringComparison.OrdinalIgnoreCase))
                    {
                        foundIdx = i;
                        break;
                    }
                    else
                    {
                        // try resolving types and compare Types if needed
                        var t1 = ResolveType(e.componentTypeName);
                        var t2 = ResolveType(mandatoryType);
                        if (t1 != null && t2 != null && t1 == t2)
                        {
                            foundIdx = i;
                            break;
                        }
                    }
                }

                if (foundIdx >= 0 && !mandatoryIndices.Contains(foundIdx))
                {
                    mandatoryIndices.Add(foundIdx);
                }
                else
                {
                    Debug.LogError($"[RoleAssigner] Mandatory role script '{mandatoryType}' does not match any roleEntries componentTypeName. Please ensure the roleEntries contain that script.");
                }
            }
        }

        // If playerCount < mandatory count, warn and reduce mandatory list to playerCount length
        if (playerCount < mandatoryIndices.Count)
        {
            Debug.LogWarning($"[RoleAssigner] Player count ({playerCount}) is less than mandatory role count ({mandatoryIndices.Count}). Only first {playerCount} mandatory roles will be used.");
            mandatoryIndices = mandatoryIndices.Take(playerCount).ToList();
        }

        // Remove mandatory indices from the pool
        foreach (var mi in mandatoryIndices)
            indices.Remove(mi);

        // Build selectedIndices list
        var selectedIndices = new List<int>();

        // Add mandatory first
        selectedIndices.AddRange(mandatoryIndices);

        // Fill remaining with shuffled indices
        Shuffle(indices);
        int needed = Mathf.Max(0, playerCount - selectedIndices.Count);
        selectedIndices.AddRange(indices.Take(needed));

        // If still not enough (roleEntries < playerCount), allow duplicates
        if (selectedIndices.Count < playerCount)
        {
            Debug.LogWarning("[RoleAssigner] Not enough distinct roles for all players; allowing duplicates to fill remaining slots.");
            var rnd = new System.Random();
            while (selectedIndices.Count < playerCount)
            {
                int pick = rnd.Next(0, roleEntries.Length);
                selectedIndices.Add(pick);
            }
        }

        // Shuffle final selectedIndices so mandatory are not always first-to-first mapping
        Shuffle(selectedIndices);
        Shuffle(playerObjects);

        assignedRoles.Clear();

        for (int i = 0; i < playerCount; i++)
        {
            var (clientId, go) = playerObjects[i];
            int idx = selectedIndices[i];
            var entry = roleEntries[idx];
            string roleName = entry.roleName ?? $"Role{idx}";

            // Try to add component by type name (if provided)
            if (!string.IsNullOrEmpty(entry.componentTypeName))
            {
                TryAddRoleComponentFromTypeName(go, entry.componentTypeName);
            }

            assignedRoles[clientId] = roleName;

            // Tell the target client to show UI
            var clientParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } } };
            ShowRoleClientRpc(idx, roleName, clientParams);

            var pm = go.GetComponent<PlayerMovement>() ?? go.GetComponentInChildren<PlayerMovement>() ?? go.GetComponentInParent<PlayerMovement>();
            if (pm != null) pm.AssignedRole.Value = new FixedString128Bytes(roleName);
            

            // --- 만약 이 역할이 "Carrier" 라면: carrier 컴포넌트 활성화 + 보균자 전용 알림 전송 ---
            if (string.Equals(roleName, "Carrier", StringComparison.OrdinalIgnoreCase))
            {
                var carrierComp = go.GetComponent<carrier>() ?? go.GetComponentInChildren<carrier>() ?? go.GetComponentInParent<carrier>();
                if (carrierComp != null)
                {
                    carrierComp.AssignAsCarrierServer();

                    // 보균자 전용 알림 (오직 해당 클라이언트에게)
                    var clientParamsCarrier = new ClientRpcParams
                    {
                        Send = new ClientRpcSendParams { TargetClientIds = new[] { clientId } }
                    };
                    ShowCarrierAssignedClientRpc("You have been selected as the Carrier. Press Z to infect other players.", clientParamsCarrier);
                }
            }

            if (debugLog) Debug.Log($"[RoleAssigner] assigned '{roleName}' -> client {clientId}, go:{go.name}");
        }
    }

    [ClientRpc]
    private void ShowCarrierAssignedClientRpc(string message, ClientRpcParams rpcParams = default)
    {
        // 클라이언트에서 개인 알림 표시
        var pm = FindFirstObjectByType<PersonalNotificationManager>();
        pm?.ShowPersonalMessage(message);
    }


    private void TryAddRoleComponentFromTypeName(GameObject target, string typeName)
    {
        if (target == null || string.IsNullOrEmpty(typeName)) return;

        Type t = ResolveType(typeName);
        if (t == null)
        {
            if (debugLog) Debug.LogWarning($"[RoleAssigner] Could not resolve type '{typeName}'");
            return;
        }

        if (!typeof(MonoBehaviour).IsAssignableFrom(t))
        {
            if (debugLog) Debug.LogWarning($"[RoleAssigner] Type '{t.FullName}' is not a MonoBehaviour, skipping AddComponent.");
            return;
        }

        // Avoid duplicate
        if (target.GetComponent(t) == null)
        {
            target.AddComponent(t);
            if (debugLog) Debug.Log($"[RoleAssigner] Added component '{t.FullName}' to {target.name}");
        }
    }

    // ---------- Client side ----------
    [ClientRpc]
    private void ShowRoleClientRpc(int roleIndex, string roleName, ClientRpcParams rpcParams = default)
    {
        if (!IsClient) return;
        StartCoroutine(ClientRevealCoroutine(roleIndex, roleName));
    }

    private IEnumerator ClientRevealCoroutine(int roleIndex, string roleName)
    {
        // set UI content
        if (roleDisplayImage != null)
        {
            if (roleEntries != null && roleIndex >= 0 && roleIndex < roleEntries.Length)
                roleDisplayImage.sprite = roleEntries[roleIndex].roleSprite;
            else
                roleDisplayImage.sprite = null;
        }
        if (roleDisplayText != null)
            roleDisplayText.text = roleName;

        if (blackScreenImage != null)
            EnsureTopCanvas(blackScreenImage.gameObject, 20000);

        if (roleCanvasGroup != null)
            EnsureTopCanvas(roleCanvasGroup.gameObject, 20001);

        // 활성화 보장 (다른 매니저가 꺼놨을 수 있으니)
        if (blackScreenImage != null && !blackScreenImage.gameObject.activeInHierarchy)
            blackScreenImage.gameObject.SetActive(true);

        if (roleCanvasGroup != null && !roleCanvasGroup.gameObject.activeInHierarchy)
            roleCanvasGroup.gameObject.SetActive(true);

        // fade in black screen
        if (blackScreenImage != null)
        {
            blackScreenImage.raycastTarget = true;
            Color c = blackScreenImage.color;
            float t = 0f;
            float start = c.a;
            while (t < fadeDuration)
            {
                t += Time.deltaTime;
                c.a = Mathf.Lerp(start, darkenTargetAlpha, t / fadeDuration);
                blackScreenImage.color = c;
                yield return null;
            }
            c.a = darkenTargetAlpha;
            blackScreenImage.color = c;
        }

        // fade in role canvas
        if (roleCanvasGroup != null)
        {
            roleCanvasGroup.interactable = true;
            roleCanvasGroup.blocksRaycasts = true;
            float t = 0f;
            float start = roleCanvasGroup.alpha;
            while (t < fadeDuration)
            {
                t += Time.deltaTime;
                roleCanvasGroup.alpha = Mathf.Lerp(start, 1f, t / fadeDuration);
                yield return null;
            }
            roleCanvasGroup.alpha = 1f;
        }

        // show duration
        yield return new WaitForSeconds(showDuration);

        // fade out role canvas
        if (roleCanvasGroup != null)
        {
            float t = 0f;
            float start = roleCanvasGroup.alpha;
            while (t < fadeDuration)
            {
                t += Time.deltaTime;
                roleCanvasGroup.alpha = Mathf.Lerp(start, 0f, t / fadeDuration);
                yield return null;
            }
            roleCanvasGroup.alpha = 0f;
            roleCanvasGroup.interactable = false;
            roleCanvasGroup.blocksRaycasts = false;
        }

        // fade out black screen
        if (blackScreenImage != null)
        {
            Color c = blackScreenImage.color;
            float t = 0f;
            float start = c.a;
            while (t < fadeDuration)
            {
                t += Time.deltaTime;
                c.a = Mathf.Lerp(start, 0f, t / fadeDuration);
                blackScreenImage.color = c;
                yield return null;
            }
            c.a = 0f;
            blackScreenImage.color = c;
            blackScreenImage.raycastTarget = false;
        }
    }

    // ---------- Utilities ----------
    private void Shuffle<T>(IList<T> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            int r = UnityEngine.Random.Range(i, list.Count);
            T tmp = list[i];
            list[i] = list[r];
            list[r] = tmp;
        }
    }

    private Type ResolveType(string typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return null;

        // 1) assembly-qualified name 가능성 검사
        try
        {
            var t = Type.GetType(typeName);
            if (t != null) return t;
        }
        catch { /* ignore */ }

        // 2) 단순 타입명(예: Namespace.ClassName 또는 ClassName) -> 모든 어셈블리 검색
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(typeName);
                if (t != null) return t;
            }
            catch { }
        }

        // 3) 마지막 시도: 어셈블리의 모든 타입을 비교(이름만)
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types = null;
            try { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var tt in types)
            {
                if (tt == null) continue;
                if (tt.Name == typeName || tt.FullName == typeName)
                    return tt;
            }
        }

        return null;
    }

#if UNITY_EDITOR
    // 에디터에서 roleScript / mandatoryRoleScript 선택 시 componentTypeName을 자동 채움
    private void OnValidate()
    {
        if (roleEntries != null)
        {
            foreach (var e in roleEntries)
            {
                if (e == null) continue;
                e.EditorFillTypeName();
            }
        }

        if (mandatoryRoles != null)
        {
            foreach (var m in mandatoryRoles)
            {
                if (m == null) continue;
                m.EditorFillTypeName();
            }
        }
    }
#endif

    private void EnsureTopCanvas(GameObject go, int sortingOrder = 10000)
    {
        if (go == null) return;

        // 먼저 현재 오브젝트가 이미 Canvas를 가지고 있는지 탐색
        Canvas c = go.GetComponentInParent<Canvas>(true);
        if (c != null)
        {
            // 기존 캔버스를 최상단으로 만들기
            c.overrideSorting = true;
            c.sortingOrder = sortingOrder;
            return;
        }

        // 없다면 임시 캔버스를 생성해서 이 오브젝트를 그 아래로 옮김
        var canvasGO = new GameObject("RoleUI_TopCanvas");
        var canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.overrideSorting = true;
        canvas.sortingOrder = sortingOrder;
        canvasGO.AddComponent<UnityEngine.UI.GraphicRaycaster>();

        // 부모를 새 캔버스로 변경 (world position 유지)
        var originalParent = go.transform.parent;
        go.transform.SetParent(canvasGO.transform, worldPositionStays: true);
    }
}
