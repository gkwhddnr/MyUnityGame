using System.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

public class DoorVisuals : NetworkBehaviour, IInteractable
{
    private BoxCollider doorCollider;

    [SerializeField] private float rotationSpeed = 2f;
    [SerializeField] private float angleWhenOpeningForward = 90f;
    [SerializeField] private float angleWhenOpeningBackward = -90f;

    [SerializeField] private Transform doorModel;
    [SerializeField] private Transform detectionPoint;
    [SerializeField] private Transform doorPivot;
    private NetworkVariable<float> lastOpenedAngle = new NetworkVariable<float>(90f,
    NetworkVariableReadPermission.Everyone,
    NetworkVariableWritePermission.Server
    );

    private Vector3 closedColliderCenter;
    private Vector3 openColliderCenter;
    [SerializeField] private Vector3 detectionSize = new Vector3(1f, 2f, 1f);
    private Vector3 detectionOffset = new Vector3(0f, 1f, 1f);
    [SerializeField] private LayerMask zombieLayer;

    public AudioSource doorAudioSource;
    public AudioClip openDoorSound;
    public AudioClip closeDoorSound;

    private Collider[] allColliders;


    private NetworkVariable<bool> isOpen = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );
    private NetworkVariable<bool> isBusy = new NetworkVariable<bool>(
        false,
        NetworkVariableReadPermission.Everyone,
        NetworkVariableWritePermission.Server
    );

    private PersonalNotificationManager notificationManagerInstance;

    public bool GetIsOpen() { return isOpen.Value; }
    public bool GetIsBusy() { return isBusy.Value; }

    private void UpdateDetectionPoint()
    {
        if (detectionPoint != null)
            detectionPoint.position = transform.position + transform.rotation * detectionOffset;
    }

    void UpdateCollider(bool open)
    {
        doorCollider.isTrigger = open;
        if (open)
        {
            doorCollider.size = new Vector3(0.1f, doorCollider.size.y, doorCollider.size.z);
            doorCollider.center = new Vector3(-0.45f, 1f, 0f); // 문이 닫혔을 때보다 안쪽으로 center 이동

        }
        else
        {
            doorCollider.size = new Vector3(1f, 2f, 1f);
            doorCollider.center = closedColliderCenter;

        }
        StartCoroutine(RefreshCollider());
    }


    private void Awake()
    {
        doorCollider = GetComponent<BoxCollider>();
        if (doorModel == null) doorModel = transform;
        if (detectionPoint == null) detectionPoint = transform;
        isOpen.Value = false;

        closedColliderCenter = doorCollider.center;
        openColliderCenter = closedColliderCenter + new Vector3(2f, 0, 0);

        allColliders = GetComponentsInChildren<Collider>(includeInactive: true);
    }

    public override void OnNetworkSpawn()
    {
        isOpen.OnValueChanged += OnDoorStateChanged;

        if (IsServer)
        {
            isOpen.Value = false;
            UpdateDetectionPoint();
        }

        if (IsClient)
        {
            notificationManagerInstance = PersonalNotificationManager.Instance;
            UpdateDoorClientRpc(isOpen.Value);
        }
        if (IsServer)
        {
            OnDoorStateChanged(isOpen.Value, isOpen.Value);
            UpdateCollider(isOpen.Value);
        }
    }

    public override void OnNetworkDespawn()
    {
        if (NetworkManager.Singleton != null)
        {
            isOpen.OnValueChanged -= OnDoorStateChanged;
        }
    }

    [ServerRpc(RequireOwnership = false)]
    public void InteractServerRpc(ulong clientId, ServerRpcParams rpcParams = default)
    {
        if (!IsServer || isBusy.Value) return;

        isBusy.Value = true;
        bool targetState = !isOpen.Value;

        if (isOpen.Value && !targetState)
        {
            if (IsPlayerBlocking(out ulong blockingPlayerId))
            {
                DenyCloseClientRpc(new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new ulong[] { clientId } } });
                isBusy.Value = false;
                return;
            }
        }

        PushOutZombiesBeforeClose();
        isOpen.Value = targetState;
        float chosenAngle;

        if (targetState) // 열기 시도할 때
        {
            Transform playerTransform = NetworkManager.Singleton.ConnectedClients[clientId].PlayerObject.transform;
            Vector3 localPlayerPos = doorPivot.InverseTransformPoint(playerTransform.position);

            // 문 기준으로 플레이어가 오른쪽에 있으면 시계 방향, 왼쪽이면 반시계
            float crossY = Vector3.Cross(doorPivot.forward, (playerTransform.position - doorPivot.position).normalized).y;

            // 문이 가로로 배치된 경우엔 localPlayerPos.z 판단
            bool isHorizontal = Mathf.Abs(localPlayerPos.x) > Mathf.Abs(localPlayerPos.z); // or tag/flag로 구분

            chosenAngle = isHorizontal ? (localPlayerPos.x > 0f ? angleWhenOpeningForward : angleWhenOpeningBackward) : (localPlayerPos.z > 0f ? angleWhenOpeningForward : angleWhenOpeningBackward);

            lastOpenedAngle.Value = chosenAngle;
        }

        else
        {
            chosenAngle = lastOpenedAngle.Value;
        }


        UpdateDoorClientRpc(isOpen.Value);
        NotifyDoorStateClientRpc(isOpen.Value, clientId);
        NotifyDoorStateWithAngleClientRpc(isOpen.Value, chosenAngle);
        StartCoroutine(ResetBusyAfterDelay());
    }



    private bool IsPlayerBlocking(out ulong blockingPlayerId)
    {
        Collider[] hits = Physics.OverlapBox(detectionPoint.position, detectionSize / 2f, detectionPoint.rotation, LayerMask.GetMask("Player"));

        foreach (var hit in hits)
        {
            var netObj = hit.GetComponentInParent<NetworkObject>();
            if (netObj != null && netObj.IsPlayerObject)
            {
                blockingPlayerId = netObj.OwnerClientId;
                return true;
            }
        }
        blockingPlayerId = ulong.MaxValue;
        return false;
    }

    private void OnDoorStateChanged(bool oldValue, bool newValue)
    {
        UpdateDoorClientRpc(newValue);
    }

    private void PushOutZombiesBeforeClose()
    {
        Collider doorCol = doorCollider;

        Collider[] zombies = Physics.OverlapBox(
            doorCollider.bounds.center,
            doorCollider.bounds.extents + Vector3.one * 0.2f,
            transform.rotation,
            zombieLayer,
            QueryTriggerInteraction.Ignore
        );

        foreach (var zomCol in zombies)
        {
            // ComputePenetration으로 최소 분리 벡터 계산
            if (Physics.ComputePenetration(
                doorCol, doorCol.transform.position, doorCol.transform.rotation,
                zomCol, zomCol.transform.position, zomCol.transform.rotation,
                out Vector3 direction, out float distance))
            {
                // 좀비를 분리 벡터만큼 이동
                var rb = zomCol.attachedRigidbody;
                Vector3 push = direction * distance;
                if (rb != null)
                    rb.MovePosition(rb.position + push);
                else
                    zomCol.transform.position += push;
            }

            // AI 리셋: 끼임 방지 후 탐색 재개
            if (zomCol.TryGetComponent<ZombieController>(out var controller))
                controller.ResetStuck();
        }
    }


    private IEnumerator ResetBusyAfterDelay()
    {
        yield return new WaitForSeconds(1f); // 문 자체 딜레이
        isBusy.Value = false;
    }

    private IEnumerator RotateDoor(bool open, float angle)
    {
        gameObject.layer = LayerMask.NameToLayer("DoorMoving");
        float elapsed = 0f;
        float duration = 1f / rotationSpeed;

        // 현재 회전 각도와 목표 각도 계산
        float currentY = doorModel.localEulerAngles.y;
        float targetY = open
            ? (currentY + angle)
            : (currentY - angle);

        // DeltaAngle로 부드럽게 보정
        float totalAngle = Mathf.DeltaAngle(currentY, targetY);
        float rotated = 0f;

        while (elapsed < duration)
        {
            float deltaTime = Time.deltaTime;
            float step = (totalAngle / duration) * deltaTime;
            doorModel.RotateAround(doorPivot.position, Vector3.up, step);
            rotated += step;

            elapsed += deltaTime;
            yield return null;
        }

        // 정밀 보정
        float correction = totalAngle - rotated;
        if (Mathf.Abs(correction) > 0.01f)
            doorModel.RotateAround(doorPivot.position, Vector3.up, correction);

        gameObject.layer = LayerMask.NameToLayer("Door");

        yield return new WaitForSeconds(0.5f);

        foreach (var col in allColliders)
        {
            col.isTrigger = open;
        }

        UpdateCollider(open);
        StartCoroutine(RefreshCollider());
    }

    IEnumerator RefreshCollider()
    {
        doorCollider.enabled = false;
        yield return new WaitForFixedUpdate();
        doorCollider.enabled = true;
    }


    [ClientRpc]
    private void UpdateDoorClientRpc(bool open)
    {

        StopAllCoroutines();

        foreach (var col in allColliders)
        {
            col.isTrigger = true;
        }

        float targetAngle = open ? lastOpenedAngle.Value : 0f;
        float currentY = doorModel.localEulerAngles.y;

        // 이미 회전되어 있는 상태라면 애니메이션 생략
        if (Mathf.Abs(Mathf.DeltaAngle(currentY, targetAngle)) < 1f)
            return;

        StartCoroutine(RotateDoor(open, targetAngle));
        UpdateCollider(open);
    }


    [ClientRpc]
    private void DenyCloseClientRpc(ClientRpcParams rpcParams = default)
    {
        // 안전 체크: NetworkManager / LocalClient / PlayerObject / PlayerInteraction / PersonalUI 모두 확인
        if (NetworkManager.Singleton == null) return;

        var localClient = NetworkManager.Singleton.LocalClient;
        if (localClient == null) return;

        var playerObj = localClient.PlayerObject;
        if (playerObj == null) return;

        var playerInteraction = playerObj.GetComponent<PlayerInteraction>();
        if (playerInteraction == null) return;

        var personalUI = playerInteraction.GetPersonalUI();
        if (personalUI == null) return;

        personalUI.ShowPersonalMessage("<color=yellow>플레이어가 문 사이에 있어서 닫히지 않았습니다.</color>");
    }

    [ClientRpc]
    private void NotifyDoorStateClientRpc(bool isNowOpen, ulong targetClientId, ClientRpcParams rpcParams = default)
    {
        // 안전 체크: NetworkManager 존재 여부
        if (NetworkManager.Singleton == null) return;

        // 타겟 비교: 서버가 이미 특정 클라이언트에게만 보냈다면 여기서 체크는 빨리 끝남
        if (NetworkManager.Singleton.LocalClientId != targetClientId) return;

        var localClient = NetworkManager.Singleton.LocalClient;
        if (localClient == null) return;

        var playerObj = localClient.PlayerObject;
        // playerObj가 없는 경우, PersonalNotificationManager를 씬에서 찾아서 사용해본다 (fallback)
        PersonalNotificationManager personalUI = null;

        if (playerObj != null)
        {
            var playerInteraction = playerObj.GetComponent<PlayerInteraction>();
            if (playerInteraction != null)
                personalUI = playerInteraction.GetPersonalUI();
        }

        // fallback: 씬에서 찾아본다 (FindFirstObjectByType 사용; 프로젝트에 따라 안전성 확인)
        if (personalUI == null)
        {
            try
            {
                personalUI = FindFirstObjectByType<PersonalNotificationManager>(FindObjectsInactive.Include);
            }
            catch
            {
                personalUI = null;
            }
        }

        if (personalUI == null) return;

        if (doorAudioSource != null)
        {
            if (isNowOpen)
            {
                if (openDoorSound != null)
                {
                    doorAudioSource.PlayOneShot(openDoorSound);
                }
            }
            else
            {
                if (closeDoorSound != null)
                {
                    doorAudioSource.PlayOneShot(closeDoorSound);
                }
            }
        }

        string msg = isNowOpen ? "<color=green>문이 열렸습니다.</color>" : "<color=red>문이 닫혔습니다.</color>";
        personalUI.ShowPersonalMessage(msg);
    }


    [ClientRpc]
    private void NotifyDoorStateWithAngleClientRpc(bool open, float angle, ClientRpcParams rpcParams = default)
    {
        StopAllCoroutines();
        StartCoroutine(RotateDoor(open, angle));
        UpdateCollider(open);
    }


    void OnDrawGizmosSelected()
    {
        if (detectionPoint == null) detectionPoint = transform;
        Gizmos.color = Color.red;
        Gizmos.DrawWireCube(detectionPoint.position, detectionSize);
    }
}