using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;

public class CameraFollow : NetworkBehaviour
{
    // 위에서 아래로 3인칭 카메라로 보여주는 스크립트
    private Transform cameraTransform;
    public Transform followTarget;
    public Vector3 cameraOffset = new Vector3(0f, 8f, -10f);

    [Header("쿼터뷰 설정")]
    [SerializeField] private float lookHeight = 1.6f;   // 캐릭터의 머리 높이 보정
    [SerializeField] private float lookAhead = 0.6f;   // 약간 앞쪽을 보도록 (전방 바이어스)

    [Header("Yaw / 회전 보정")]
    [Tooltip("W (앞) 누른 상태에서 A/D로 얼마나 빠르게 카메라를 좌우로 회전시킬지")]
    public float lateralRotateSpeed = 90f; 
    [Tooltip("Yaw가 캐릭터 회전에 맞춰 복귀할 때 부드럽게 보정되는 속도")]
    public float yawReturnSmooth = 5f;
    private float currentYaw = 0f; // 현재 카메라 회전(Y축 각도)

    private Quaternion lockedRotation = Quaternion.identity;

    // 3인칭 카메라로 할 것인지 자유시점으로 할 건지 정하기
    private bool isFollowing = true;
    private bool isReturningToPlayer = false;
    private bool forceFreeLook = false;

    public float cameraMoveSpeed = 10f;
    public float edgeSize = 20f;
    public float smoothLerpSpeed = 5f;

    // 자유 시점 -> 3인칭 시야로 이동하는 과정을 스무스하게 보여주기
    private Transform externalTarget = null;
    private bool subscribedToManager = false;
    private ulong localPlayerContainerNetworkId = 0;

    [Header("Occlusion -> TopView")]
    [Tooltip("가림 체크에 사용할 레이어 (Default, Wall 등)")]
    public LayerMask occlusionLayerMask = 1 << 0; // 기본 Default 레이어
    [Tooltip("가림 체크 간격 (초) — 0이면 매프레임 체크")]
    public float occlusionCheckInterval = 0.12f;
    [Tooltip("탑뷰(오버헤드)로 전환할 때 사용할 오프셋 (플레이어 위로 완전 오버헤드)")]
    public Vector3 topViewOffset = new Vector3(0f, 10f, 0f);
    [Tooltip("탑뷰 모드일 때 카메라가 목표를 바라보는 방식: true면 정확히 수직으로, false면 lookAhead/lookHeight 사용")]
    public bool topViewLookStraightDown = true;
    [Tooltip("오프셋 전환 부드러움 (크면 느리게 전환)")]
    public float offsetTransitionSpeed = 6f;
    [Tooltip("탑뷰로 강제 전환할 최소 거리(플레이어->카메라)")]
    public float occlusionTriggerMinDistance = 3f;

    [Header("Quarter-view Rotation")]
    [Tooltip("쿼터뷰에서의 고정 내림 각도(양수)")]
    public float quarterViewPitch = 45f;
    [Tooltip("카메라 회전의 보간 속도")]
    public float rotationSmoothSpeed = 8f;

    private Vector3 desiredCameraOffset; // 월드오프셋(실제 사용할)
    private Vector3 currentCameraOffset;
    private float lastOcclusionCheckTime = -10f;
    private bool isTopViewActive = false;
    private bool wasMovementInput = false;

    public override void OnNetworkSpawn()
    {
        if (!IsOwner) return;

        cameraTransform = Camera.main != null ? Camera.main.transform : null;

        // 안전하게 local player container가 준비될 때까지 기다리는 코루틴 실행
        StartCoroutine(WaitAndBindToCharacterManager());
    }

    private IEnumerator WaitAndBindToCharacterManager()
    {
        // 1) 먼저 로컬 플레이어의 NetworkObject(컨테이너)를 얻는다.
        float start = Time.time;
        float timeout = 5f;
        NetworkObject localPlayerObj = null;
        while (Time.time - start < timeout)
        {
            if (NetworkManager.Singleton != null)
            {
                localPlayerObj = NetworkManager.Singleton.SpawnManager.GetLocalPlayerObject();
                if (localPlayerObj != null) break;
            }
            yield return null;
        }

        if (localPlayerObj == null)
        {
            Debug.LogWarning("[CameraFollow] Local player NetworkObject not found within timeout.");
            yield break;
        }

        localPlayerContainerNetworkId = localPlayerObj.NetworkObjectId;

        // 2) PlayerCharacterManager 찾기
        var manager = FindFirstObjectByType<PlayerCharacterManager>();
        if (manager == null)
        {
            Debug.LogWarning("[CameraFollow] PlayerCharacterManager not found in scene.");
            yield break;
        }

        // 3) 이미 spawnedCharacters에 내 캐릭터가 있나 확인
        if (manager.spawnedCharacters != null && manager.spawnedCharacters.TryGetValue(localPlayerContainerNetworkId, out GameObject existing))
        {
            SetFollowTarget(existing.transform);
            yield break;
        }

        // 4) 없다면 이벤트 구독(생성될 때 잡기) + slotDataList onchange 대비
        if (!subscribedToManager)
        {
            manager.OnCharacterSpawned += OnCharacterSpawned;
            // slotDataList는 public NetworkVariable in your manager — 구독해도 좋음 (null체크)
            if (manager.slotDataList != null)
                manager.slotDataList.OnValueChanged += OnSlotDataChanged;

            subscribedToManager = true;
        }

        // 5) 또한, SpawnedObjects 쪽 타이밍 문제를 대비해 잠깐 대기하면서 폴링도 해본다 (1초 내)
        float pollStart = Time.time;
        while (Time.time - pollStart < 1.0f)
        {
            if (manager.spawnedCharacters != null && manager.spawnedCharacters.TryGetValue(localPlayerContainerNetworkId, out GameObject found))
            {
                SetFollowTarget(found.transform);
                yield break;
            }
            yield return null;
        }

        // 끝. 이제 이벤트 옵저버가 캐릭터 생성 시 자동으로 SetFollowTarget을 호출함.
    }

    private void OnCharacterSpawned(GameObject character)
    {
        // 내 캐릭터인지 확인
        var parentNetObj = character.GetComponentInParent<NetworkObject>();
        if (parentNetObj != null && parentNetObj.NetworkObjectId == localPlayerContainerNetworkId)
        {
            SetFollowTarget(character.transform);
        }
    }

    private void OnSlotDataChanged(PlayerSlotDataList oldList, PlayerSlotDataList newList)
    {
        var manager = FindFirstObjectByType<PlayerCharacterManager>();
        if (manager == null) return;

        foreach (var slot in newList.PlayerSlots)
        {
            if (slot.PlayerNetId == localPlayerContainerNetworkId)
            {
                if (manager.spawnedCharacters.TryGetValue(slot.PlayerNetId, out var charObj))
                {
                    SetFollowTarget(charObj.transform);
                }
                break;
            }
        }
    }


    void Start()
    {
        // 로컬 플레이어의 오브젝트일 때만 카메라 관련 설정
        if (IsOwner)
        {
            cameraTransform = Camera.main.transform;
            if (followTarget != null) cameraTransform.position = followTarget.position + cameraOffset;
            SetCameraPosition(cameraTransform.position);

            // 초기 currentYaw 설정 (followTarget 있으면 그 방향으로)
            if (followTarget != null)
                currentYaw = followTarget.eulerAngles.y;
            else
                currentYaw = cameraTransform.eulerAngles.y;

            currentCameraOffset = cameraOffset;
            desiredCameraOffset = cameraOffset;

            if (cameraTransform != null)
                lockedRotation = cameraTransform.rotation;
        }
        else
        {
            // 자기자신 플레이어가 아니면 스크립트 비활성화
            enabled = false;
        }
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Update()
    {
        // Start 함수에서 enabled = false로 제어되지만 안전을 위해 추가
        if (!IsOwner || cameraTransform == null) return;

        // 외부 타겟(관전)이 있으면 우선적으로 동일한 lerp/snap 로직 적용
        if (externalTarget != null)
        {
            Vector3 targetPosition = externalTarget.position + cameraOffset;

            if (isReturningToPlayer || Vector3.Distance(cameraTransform.position, targetPosition) > 0.1f)
            {
                cameraTransform.position = Vector3.Lerp(cameraTransform.position, targetPosition, Time.deltaTime * smoothLerpSpeed);
                if (Vector3.Distance(cameraTransform.position, targetPosition) < 0.1f)
                {
                    cameraTransform.position = targetPosition;
                    isReturningToPlayer = false;
                }
            }

            cameraTransform.LookAt(GetLookAtPointForTarget(externalTarget));
            return;
        }

        // 강제 자유시점이면 freelook
        if (forceFreeLook)
        {
            FreeLookMove();
            return;
        }

        if (Input.GetKeyDown(KeyCode.Y))
        {
            isFollowing = !isFollowing;

            if (isFollowing)
                isReturningToPlayer = true;
            else
                isReturningToPlayer |= false;
        }

        bool movementInput = Mathf.Abs(Input.GetAxisRaw("Horizontal")) > 0f || Mathf.Abs(Input.GetAxisRaw("Vertical")) > 0f;

        if (followTarget != null && (occlusionCheckInterval <= 0f || Time.time - lastOcclusionCheckTime >= occlusionCheckInterval))
        {
            lastOcclusionCheckTime = Time.time;
            CheckOcclusionAndSetTopView();
        }

        desiredCameraOffset = isTopViewActive ? topViewOffset : cameraOffset;
        currentCameraOffset = Vector3.Lerp(currentCameraOffset, desiredCameraOffset, Time.deltaTime * offsetTransitionSpeed);

        if (isFollowing && followTarget != null)
        {
            Vector3 targetPosition = followTarget.position + currentCameraOffset;

            // 항상 위치를 따라가도록 (movementInput에 의존하지 않음)
            if (!isTopViewActive)
            {
                if (isReturningToPlayer || Vector3.Distance(cameraTransform.position, targetPosition) > 0.01f)
                {
                    cameraTransform.position = Vector3.Lerp(cameraTransform.position, targetPosition, Time.deltaTime * smoothLerpSpeed);
                    if (Vector3.Distance(cameraTransform.position, targetPosition) < 0.01f)
                    {
                        cameraTransform.position = targetPosition;
                        isReturningToPlayer = false;
                    }
                }
            }
            else
            {
                cameraTransform.position = Vector3.Lerp(cameraTransform.position, targetPosition, Time.deltaTime * (smoothLerpSpeed * 1.5f));
            }

            // --- 회전 처리: 플레이어의 회전에 따라 변하지 않도록 항상 lockedRotation으로 보간 ---
            Quaternion targetRotation;
            if (isTopViewActive && topViewLookStraightDown)
            {
                targetRotation = Quaternion.Euler(90f, 0f, 0f);
            }
            else
            {
                // 기본: SetFollowTarget()에서 정해진 lockedRotation을 유지
                targetRotation = lockedRotation;
            }

            cameraTransform.rotation = Quaternion.Slerp(cameraTransform.rotation, targetRotation, Time.deltaTime * rotationSmoothSpeed);
        }
        else
        {
            FreeLookMove();
        }

        wasMovementInput = movementInput;
    }

    private void CheckOcclusionAndSetTopView()
    {
        if (followTarget == null || cameraTransform == null) { isTopViewActive = false; return; }

        Vector3 lookPoint = GetLookAtPointForTarget(followTarget);
        Vector3 desiredCamPosWorld = followTarget.position + cameraOffset;
        Vector3 dir = desiredCamPosWorld - lookPoint;
        float distance = dir.magnitude;

        if (distance < 0.001f) { isTopViewActive = false; return; }
        dir /= distance;

        if (distance < occlusionTriggerMinDistance)
        {
            isTopViewActive = false;
            return;
        }

        if (Physics.Raycast(lookPoint, dir, out RaycastHit hit, distance, occlusionLayerMask, QueryTriggerInteraction.Ignore))
        {
            if (!IsColliderPartOfTarget(hit.collider, followTarget))
            {
                isTopViewActive = true;
                return;
            }
        }

        isTopViewActive = false;
    }

    private bool IsColliderPartOfTarget(Collider col, Transform target)
    {
        if (col == null || target == null) return false;
        // 같은 루트이거나 child of target 일 때 플레이어 내부
        var root = col.transform.root;
        if (root == target.root) return true;
        return col.transform.IsChildOf(target);
    }

    private Vector3 GetLookAtPointForTarget(Transform target)
    {
        if (target == null) return Vector3.zero;
        Vector3 ahead = target.forward * lookAhead;
        return target.position + Vector3.up * lookHeight + ahead;
    }

    public void SetFollowTarget(Transform character)
    {
        if (character == null) return;

        followTarget = character.transform;

        if (cameraTransform != null)
        {
            cameraTransform.position = followTarget.position + cameraOffset;
            cameraTransform.rotation = Quaternion.Euler(-Mathf.Abs(quarterViewPitch), followTarget.eulerAngles.y, 0f);
        }

        Debug.Log($"[CameraFollow] Target set to: {character.name}");
    }



    void SetCameraPosition(Vector3 positionToSet)
    {
        if (cameraTransform != null)
        {
            cameraTransform.position = positionToSet;
            if (followTarget != null)
            {
                cameraTransform.LookAt(GetLookAtPointForTarget(followTarget));
            }
        }
    }

    void FreeLookMove()
    {
        if (cameraTransform == null) return;

        // 카메라가 바라보는 방향과 오른쪽 방향을 사용
        Vector3 move = Vector3.zero;
        Vector3 camForward = Vector3.forward;
        Vector3 camRight = Vector3.right;
        Vector3 pos = Input.mousePosition;

        // 컴퓨터화면의 맨 상하좌우로 커서를 이동하면 화면이 움직임 (스타크래프트의 화면 밀기 방식)
        if (pos.x >= Screen.width - edgeSize) move += camRight;
        if (pos.x <= edgeSize) move -= camRight;
        if (pos.y >= Screen.height - edgeSize) move += camForward;
        if (pos.y <= edgeSize) move -= camForward;

        cameraTransform.position += move.normalized * cameraMoveSpeed * Time.deltaTime;
    }

    public void OnPlayerDeath()
    {
        if (IsOwner)
        {
            // CameraFollow 스크립트를 비활성화하고 SpectatorCamera에게 권한을 넘김
            enabled = false;
            if (SpectatorCamera.Instance != null)
            {
                SpectatorCamera.Instance.TakeControl(cameraTransform);
            }
        }
    }

    public override void OnDestroy()
    {
        // 이벤트 구독 해제
        var manager = FindFirstObjectByType<PlayerCharacterManager>();
        if (manager != null && subscribedToManager)
        {
            manager.OnCharacterSpawned -= OnCharacterSpawned;
            if (manager.slotDataList != null)
                manager.slotDataList.OnValueChanged -= OnSlotDataChanged;
        }

        base.OnDestroy();
    }
    public void ForceInitializeAndBind()
    {
        // 카메라 Transform이 아직 없다면 Camera.main에서 가져옴
        if (cameraTransform == null && Camera.main != null)
            cameraTransform = Camera.main.transform;

        // Update가 동작하도록 활성화(필요 시)
        enabled = true;

        // 이미 followTarget이 세팅되어 있다면 바로 리턴
        if (followTarget != null) return;

        // 로컬 캐릭터를 찾는 코루틴 실행 (최대 5초)
        StartCoroutine(DelayedBindToLocalCharacterCoroutine(5f));
    }

    private IEnumerator DelayedBindToLocalCharacterCoroutine(float timeoutSeconds)
    {
        float start = Time.time;

        while (Time.time - start < timeoutSeconds)
        {
            // Unity 버전에 따라 안전하게 PlayerMovement 인스턴스들을 얻음
#if UNITY_2023_2_OR_NEWER
            var pms = UnityEngine.Object.FindObjectsByType<PlayerMovement>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None
            );
#else
        var all = Resources.FindObjectsOfTypeAll<PlayerMovement>();
        var pms = all.Where(p => p != null && p.gameObject != null && p.gameObject.scene.IsValid() && p.gameObject.scene.isLoaded).ToArray();
#endif
            if (pms != null)
            {
                foreach (var pm in pms)
                {
                    if (pm == null) continue;
                    // 로컬 클라이언트의 소유 캐릭터인지 확인
                    if (pm.OwnerClientId == NetworkManager.Singleton.LocalClientId && pm.IsCharacterInstance())
                    {
                        SetFollowTarget(pm.transform);
                        yield break;
                    }
                }
            }

            // 짧게 대기 후 재시도
            yield return new WaitForSeconds(0.15f);
        }
    }
}