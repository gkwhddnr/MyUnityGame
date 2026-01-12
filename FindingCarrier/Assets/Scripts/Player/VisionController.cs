using UnityEngine;
using Unity.Netcode;
using System.Collections;
using System.Collections.Generic;

public class VisionController : NetworkBehaviour
{
    [SerializeField] private Light visionLight;
    private Transform playerCam;

    public float visionRange = 5f;
    private float outerAngle = 179f;

    private GameObject[] litObjects;

    private bool isPlayerDead = false;

    private DayNightManager dayNightManager;

    // 캐시: 각 Renderer / Light 의 원래 enabled 상태 저장
    private Dictionary<Renderer, bool> rendererOriginalState = new Dictionary<Renderer, bool>();
    private Dictionary<Light, bool> lightOriginalState = new Dictionary<Light, bool>();

    void Start()
    {
        visionLight = GetComponent<Light>();
        if (!IsOwner)
        {
            if (visionLight != null) visionLight.enabled = false;
            this.enabled = false;
            return;
        }

        dayNightManager = FindFirstObjectByType<DayNightManager>();

        // 플레이어 카메라 트랜스폼 저장
        var cam = GetComponentInChildren<Camera>();
        if (cam != null) playerCam = cam.transform;

        visionLight.type = LightType.Spot;
        visionLight.range = visionRange;
        visionLight.spotAngle = outerAngle;
        visionLight.enabled = true;

        // litObjects 초기 수집 (씬 내 "LitObject" 태그 가진 것들)
        litObjects = GameObject.FindGameObjectsWithTag("LitObject");

        // 초기 캐시 (각 렌더러/라이트의 원래 상태)
        CacheAllLitObjectsRenderersAndLights();
    }

    void Update()
    {
        if (!IsOwner || visionLight == null) return;
        if (isPlayerDead) return;

        // litObjects가 동적으로 바뀌었을 수 있으므로 필요한 경우만 갱신
        EnsureLitObjectsCache();

        if (isPlayerDead)
        {
            // 자신의 라이트는 끔
            if (visionLight.enabled) visionLight.enabled = false;

            // 모든 litObjects의 렌더러/라이트는 기본으로 두되(관전자라면 시야 제어X), 이름표만 강제 표시
            if (litObjects != null)
            {
                foreach (var obj in litObjects)
                {
                    if (obj == null) continue;

                    var childRenderers = obj.GetComponentsInChildren<Renderer>(true);
                    foreach (var r in childRenderers)
                    {
                        if (!rendererOriginalState.ContainsKey(r))
                            rendererOriginalState[r] = r.enabled;
                        // 관전모드에서는 렌더러 조작하지 않음 (기존 상태 보존)
                    }

                    var childLights = obj.GetComponentsInChildren<Light>(true);
                    foreach (var l in childLights)
                    {
                        if (!lightOriginalState.ContainsKey(l))
                            lightOriginalState[l] = l.enabled;
                        // 관전모드에서는 라이트 조작하지 않음
                    }

                    var pm = obj.GetComponent<PlayerMovement>();
                    if (pm != null && pm.nameText != null)
                    {
                        // 관전자는 모든 닉네임을 보게 한다
                        pm.nameText.gameObject.SetActive(true);
                    }
                }
            }

            return;
        }

        // 밤이면 다른 플레이어(그리고 litObjects에 포함된 오브젝트들)를 숨김
        if (dayNightManager != null && dayNightManager.isNight.Value)
        {
            // 자신의 라이트도 끔
            if (visionLight.enabled) visionLight.enabled = false;

            if (IsLocalClientCarrier())
            {
                // 보균자 로컬: 밤에도 모든 플레이어를 보이도록 설정
                ShowAllPlayersForCarrierNight();
            }
            else
            {
                // 비-보균자 로컬: 기존 로직으로 숨기기
                HideLitObjectsForNight();
            }
            return;
        }
        else
        {
            // 낮이면 자신의 라이트 켬
            if (!visionLight.enabled) visionLight.enabled = true;
            // 낮일 때는 캐시에 저장된 원래 상태로 복원(렌더러/라이트)
            RestoreRenderersAndLightsFromCache();
        }

        visionLight.range = visionRange;
        visionLight.spotAngle = outerAngle;

        // 빛 범위 내 오브젝트 렌더링 활성화/비활성화 (낮일 때만)
        // 대상은 litObjects (태그 "LitObject")로 제한
        if (litObjects == null) return;

        foreach (var obj in litObjects)
        {
            if (obj == null) continue;

            // 네트워크 오브젝트 (있을 수도, 없을 수도 있음)
            var netObj = obj.GetComponent<NetworkObject>();

            // --- 중요한 변경: "IsOwner" 예외는 플레이어 오브젝트에만 적용 ---
            // 플레이어 여부 판단 (PlayerMovement가 붙어있으면 플레이어로 취급)
            var playerMovement = obj.GetComponentInChildren<PlayerMovement>(true);
            bool isPlayerObject = playerMovement != null;

            bool isOwnerObject = false;
            if (isPlayerObject && netObj != null)
            {
                // 오직 Player에 대해서만 로컬 소유자 예외를 적용
                isOwnerObject = netObj.IsOwner;
            }

            bool isVisible = false;

            if (isOwnerObject)
            {
                // 자기 자신의 플레이어 오브젝트는 항상 보이도록
                isVisible = true;
            }
            else
            {
                // 거리 + 시야각 검사 (일반 오브젝트 및 좀비 포함)
                float dist = Vector3.Distance(transform.position, obj.transform.position);
                if (dist <= visionRange)
                {
                    Vector3 toTarget = (obj.transform.position - visionLight.transform.position).normalized;
                    float angle = Vector3.Angle(visionLight.transform.forward, toTarget);
                    if (angle <= outerAngle * 0.5f)
                        isVisible = true;
                }
            }

            // 렌더러/라이트 갱신 (모든 자식 렌더러/라이트 포함)
            var childRenderers = obj.GetComponentsInChildren<Renderer>(true);
            foreach (var r in childRenderers)
            {
                // 캐시에 없으면 기본 상태 저장
                if (!rendererOriginalState.ContainsKey(r))
                    rendererOriginalState[r] = r.enabled;

                r.enabled = isVisible;
            }

            // 플레이어 이름 텍스트 (있다면 보이게/숨김)
            // PlayerMovement가 자식에 있을 수 있으므로 GetComponentInChildren 사용
            var pm = obj.GetComponentInChildren<PlayerMovement>(true);
            if (pm != null && pm.nameText != null)
            {
                // 이름표는 플레이어이거나 가시적일때만 보이게
                pm.nameText.gameObject.SetActive(isVisible || isOwnerObject);
            }

            var childLights = obj.GetComponentsInChildren<Light>(true);
            foreach (var l in childLights)
            {
                if (!lightOriginalState.ContainsKey(l))
                    lightOriginalState[l] = l.enabled;

                l.enabled = isVisible;
            }
        }
    }

    void LateUpdate()
    {
        if (!IsOwner || playerCam == null || visionLight == null) return;
        if (isPlayerDead) return;

        // 라이트 방향을 카메라와 맞춤
        visionLight.transform.SetPositionAndRotation(playerCam.position, playerCam.rotation);
    }

    // 밤일 때 litObjects 안의 오브젝트 렌더러/라이트 끔 (자기자신은 제외)
    private void HideLitObjectsForNight()
    {
        if (litObjects == null) return;

        foreach (var obj in litObjects)
        {
            if (obj == null) continue;

            // 플레이어 오브젝트 자체는 로컬 소유자면 예외로 남겨둠
            var netObj = obj.GetComponent<NetworkObject>();
            var pmCheck = obj.GetComponentInChildren<PlayerMovement>(true);
            if (pmCheck != null && netObj != null && netObj.IsOwner) continue;

            var renderers = obj.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (!rendererOriginalState.ContainsKey(r))
                    rendererOriginalState[r] = r.enabled;
                r.enabled = false;
            }

            var lights = obj.GetComponentsInChildren<Light>(true);
            foreach (var l in lights)
            {
                if (!lightOriginalState.ContainsKey(l))
                    lightOriginalState[l] = l.enabled;
                l.enabled = false;
            }

            var pm = obj.GetComponentInChildren<PlayerMovement>(true);
            if (pm != null && pm.nameText != null)
            {
                if (isPlayerDead)
                {
                    pm.nameText.gameObject.SetActive(true);
                }
                else
                {
                    pm.nameText.gameObject.SetActive(false);
                }
            }
        }
    }

    // 보균자 로컬일 때 밤에 모든 플레이어를 보이게 하는 처리
    private void ShowAllPlayersForCarrierNight()
    {
        if (litObjects == null) return;

        foreach (var obj in litObjects)
        {
            if (obj == null) continue;

            bool isVisible = false;
            float dist = Vector3.Distance(transform.position, obj.transform.position);
            if (dist <= visionRange)
            {
                Vector3 toTarget = (obj.transform.position - visionLight.transform.position).normalized;
                float angle = Vector3.Angle(visionLight.transform.forward, toTarget);
                if (angle <= outerAngle * 0.5f) isVisible = true;
            }

            // 렌더러/라이트는 isVisible이면 활성화, 아니면 비활성화
            var childRenderers = obj.GetComponentsInChildren<Renderer>(true);
            foreach (var r in childRenderers)
            {
                if (!rendererOriginalState.ContainsKey(r)) rendererOriginalState[r] = r.enabled;
                r.enabled = isVisible;
            }

            var childLights = obj.GetComponentsInChildren<Light>(true);
            foreach (var l in childLights)
            {
                if (!lightOriginalState.ContainsKey(l)) lightOriginalState[l] = l.enabled;
                l.enabled = isVisible;
            }

            // 닉네임은 항상 숨김 (요청대로)
            var pm = obj.GetComponentInChildren<PlayerMovement>(true);
            if (pm != null && pm.nameText != null)
            {
                pm.nameText.gameObject.SetActive(false);
            }
        }
    }

    // 낮으로 돌아올 때 캐시에 저장된 원상태로 복원
    private void RestoreRenderersAndLightsFromCache()
    {
        // 렌더러 복원
        var toRemoveR = new List<Renderer>();
        foreach (var kv in rendererOriginalState)
        {
            var r = kv.Key;
            if (r == null) { toRemoveR.Add(r); continue; }
            r.enabled = kv.Value;
        }
        // 라이트 복원
        var toRemoveL = new List<Light>();
        foreach (var kv in lightOriginalState)
        {
            var l = kv.Key;
            if (l == null) { toRemoveL.Add(l); continue; }
            l.enabled = kv.Value;
        }
    }

    // 씬 시작 시 litObjects의 모든 렌더러/라이트 상태를 캐시
    private void CacheAllLitObjectsRenderersAndLights()
    {
        if (litObjects == null) return;
        foreach (var obj in litObjects)
        {
            if (obj == null) continue;
            var renderers = obj.GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (!rendererOriginalState.ContainsKey(r))
                    rendererOriginalState[r] = r.enabled;
            }
            var lights = obj.GetComponentsInChildren<Light>(true);
            foreach (var l in lights)
            {
                if (!lightOriginalState.ContainsKey(l))
                    lightOriginalState[l] = l.enabled;
            }
        }
    }

    public void RefreshLitObjectsCache()
    {
        EnsureLitObjectsCache();
        CacheAllLitObjectsRenderersAndLights();
    }


    // litObjects 배열이 null 이거나 장면의 태그 갯수와 다를 때만 갱신 (비용 최소화)
    private void EnsureLitObjectsCache()
    {
        var found = GameObject.FindGameObjectsWithTag("LitObject");
        if (litObjects == null || found.Length != litObjects.Length)
        {
            litObjects = found;
            CacheAllLitObjectsRenderersAndLights();
        }
    }

    public void DisableLightForNight()
    {
        if (visionLight != null)
            visionLight.enabled = false;

        HideLitObjectsForNight();
    }

    public void EnableLightForDay()
    {
        if (visionLight != null)
            visionLight.enabled = true;

        RestoreRenderersAndLightsFromCache();
    }

    // 밤에 자신의 라이트(시야) 끄기
    [ClientRpc]
    public void SetNightVisionClientRpc(ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        if (visionLight != null) visionLight.enabled = false;

        if (IsLocalClientCarrier())
            ShowAllPlayersForCarrierNight();
        else
            HideLitObjectsForNight();
    }

    // 아침에 자신의 라이트(시야) 켜기
    [ClientRpc]
    public void SetDayVisionClientRpc(ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;
        visionLight.enabled = true;
        RestoreRenderersAndLightsFromCache();
    }

    // 은신 시 Point 시야
    public void SwitchToPointView()
    {
        if (!IsOwner || visionLight == null) return;

        StopAllCoroutines();
        StartCoroutine(FadeLightIntensity(visionLight.intensity, 0f, 2f));

        if (visionLight != null)
        {
            visionLight.type = LightType.Point;
            visionLight.range = visionRange;
        }
    }

    // 복귀 시 기본 Spot 시야
    public void SwitchToNormalView()
    {
        if (!IsOwner || visionLight == null) return;

        StopAllCoroutines();
        StartCoroutine(FadeLightIntensity(visionLight.intensity, 1f, 2f));

        if (visionLight != null)
        {
            visionLight.type = LightType.Spot;
            visionLight.range = visionRange;
        }
    }

    private IEnumerator FadeLightIntensity(float from, float to, float duration)
    {
        float t = 0f;
        while (t < duration)
        {
            t += Time.deltaTime;
            float current = Mathf.Lerp(from, to, t / duration);
            visionLight.intensity = current;
            yield return null;
        }
        visionLight.intensity = to;
    }

    public void SetPlayerDead()
    {
        isPlayerDead = true;
        if (visionLight != null) visionLight.enabled = false;

        if (litObjects != null)
        {
            foreach (var obj in litObjects)
            {
                if (obj == null) continue;
                var pm = obj.GetComponent<PlayerMovement>();
                if (pm != null && pm.nameText != null)
                {
                    pm.nameText.gameObject.SetActive(true);
                }
            }
        }
    }

    // 로컬 클라이언트가 Carrier인지 판단하는 헬퍼 (client-side)
    private bool IsLocalClientCarrier()
    {
        if (NetworkManager.Singleton == null) return false;
        ulong localId = NetworkManager.Singleton.LocalClientId;
        int carrierLayerIndex = LayerMask.NameToLayer("Carrier");

        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var no = kv.Value;
            if (no == null) continue;
            if (no.OwnerClientId != localId) continue;

            var carrierComp = no.GetComponent<carrier>() ?? no.GetComponentInChildren<carrier>() ?? no.GetComponentInParent<carrier>();
            if (carrierComp != null)
            {
                try
                {
                    if (carrierComp.IsCarrier.Value) return true;
                }
                catch { }
            }

            if (carrierLayerIndex >= 0 && no.gameObject.layer == carrierLayerIndex) return true;
        }

        return false;
    }
}
