using System.Globalization;
using Unity.Netcode;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;

public class SpectatorCamera : NetworkBehaviour
{
    public static SpectatorCamera Instance { get; private set; }

    [SerializeField] private PlayerSpectatorManager playerSpectatorManager;

    private Transform cameraTransform;
    private Transform currentTarget;
    private bool isFollowing = true;
    private int currentSpectatingSlot = 0;

    public Vector3 cameraOffset = new Vector3(0, 10, 0);
    public float cameraMoveSpeed = 10f;
    public float edgeSize = 20f;
    public float smoothLerpSpeed = 5f;
    private bool isReturningToTarget = false;

    private bool revealAllObjects = false;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        enabled = false;

        if (playerSpectatorManager == null)
            // 인스펙터에 없으면 인스턴스에서 찾아본다 (대부분 자동으로 연결)
            playerSpectatorManager = PlayerSpectatorManager.Instance;
        
    }

    private void Update()
    {
        if (cameraTransform == null) return;

        if (revealAllObjects)
        {
            foreach (var obj in GameObject.FindGameObjectsWithTag("LitObject"))
            {
                if (obj == null) continue;
                var rend = obj.GetComponentInChildren<Renderer>();
                if (rend != null)
                {
                    rend.enabled = true;
                }
            }
        }

        HandleSpectateInput();
        HandleCameraMovement();
    }

    public void TakeControl(Transform mainCameraTransform)
    {
        cameraTransform = mainCameraTransform;
        enabled = true;
        SetFreeLookMode(true);
        revealAllObjects = true;
    }

    private void HandleSpectateInput()
    {
        for (int i = 1; i <= 8; i++)
        {
            if (Input.GetKeyDown(KeyCode.Alpha0 + i))
            {
                HandleSpectateKey(i);
            }
        }

        if (Input.GetKeyDown(KeyCode.Y))
        {
            SetFreeLookMode(!isFollowing);
        }
    }

    private void HandleCameraMovement()
    {
        if (isFollowing)
        {
            if (currentTarget != null)
            {
                // CameraFollow의 isFollowing 블록과 동일한 동작 (lerp + snap)
                Vector3 targetPosition = currentTarget.position + cameraOffset;

                if (isReturningToTarget || Vector3.Distance(cameraTransform.position, targetPosition) > 0.1f)
                {
                    cameraTransform.position = Vector3.Lerp(cameraTransform.position, targetPosition, Time.deltaTime * smoothLerpSpeed);
                    if (Vector3.Distance(cameraTransform.position, targetPosition) < 0.1f)
                    {
                        cameraTransform.position = targetPosition;
                        isReturningToTarget = false;
                    }
                }
            }
            else
            {
                SetFreeLookMode(true);
            }
        }
        else
        {
            FreeLookMove();
        }
    }


    private void FreeLookMove()
    {
        if (cameraTransform == null) return;

        Vector3 move = Vector3.zero;
        Vector3 camForward = Vector3.forward;
        Vector3 camRight = Vector3.right;
        Vector3 pos = Input.mousePosition;

        if (pos.x >= Screen.width - edgeSize) move += camRight;
        if (pos.x <= edgeSize) move -= camRight;
        if (pos.y >= Screen.height - edgeSize) move += camForward;
        if (pos.y <= edgeSize) move -= camForward;

        if (move.sqrMagnitude > 0f) cameraTransform.position += move.normalized * cameraMoveSpeed * Time.deltaTime;
    }

    private void HandleSpectateKey(int slot)
    {
        // 1) 먼저 PlayerSpectatorManager에서 슬롯->NetworkObject 매핑을 얻어보자.
        var specMgr = PlayerSpectatorManager.Instance;
        NetworkObject targetNetObj = null;
        if (specMgr != null)
        {
            var netObj = specMgr.GetPlayerBySlot(slot);
            if (netObj != null)
            {
                targetNetObj = netObj;
            }
        }

        // 2) 만약 슬롯 매핑이 없거나 컨테이너일 경우, 같은 소유자(OwnerClientId)의 실제 캐릭터를 SpawnedObjects에서 찾아본다.
        GameObject targetPlayer = null;
        if (targetNetObj != null)
        {
            // 만약 이 네트워크 오브젝트 자체가 캐릭터(플레이어 인스턴스)라면 바로 사용
            if (targetNetObj.TryGetComponent<PlayerMovement>(out var pm) && pm.IsCharacterInstance())
            {
                targetPlayer = targetNetObj.gameObject;
            }
            else
            {
                // 폴백: 같은 OwnerClientId를 가진 SpawnedObjects 중 PlayerMovement(IsCharacterInstance) 찾아보기
                ulong owner = targetNetObj.OwnerClientId;
                if (NetworkManager.Singleton != null)
                {
                    foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
                    {
                        var so = kv.Value;
                        if (so == null) continue;
                        if (so.OwnerClientId != owner) continue;
                        if (so.TryGetComponent<PlayerMovement>(out var pm2) && pm2.IsCharacterInstance())
                        {
                            targetPlayer = so.gameObject;
                            break;
                        }
                    }
                }
            }
        }

        // 3) 여전히 못 찾았으면 기존 폴백(기존 코드: PlayerCharacterManager spawn list / 씬 검색)을 사용
        if (targetPlayer == null)
        {
            var pcm = FindFirstObjectByType<PlayerCharacterManager>();
            if (pcm != null && pcm.characterPrefabs != null && slot >= 1 && slot <= pcm.characterPrefabs.Length)
            {
                string wantedName = pcm.characterPrefabs[slot - 1]?.name ?? "";
                // 씬의 PlayerMovement 검색 (모든 활성/비활성 포함)
#if UNITY_2023_2_OR_NEWER
                var players = UnityEngine.Object.FindObjectsByType<PlayerMovement>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
            var players = FindObjectsOfType<PlayerMovement>(true);
#endif
                foreach (var p in players)
                {
                    if (p == null) continue;
                    if (!string.IsNullOrEmpty(wantedName) && p.gameObject.name.Contains(wantedName))
                    {
                        targetPlayer = p.gameObject;
                        break;
                    }
                }
            }
        }

        // 4) 최종: 못 찾으면 자유시점, 찾으면 follow
        if (targetPlayer == null)
        {
            SetFreeLookMode(true);
            return;
        }

        if (currentSpectatingSlot == slot)
            SetFreeLookMode(true);
        else
            SetFollowTarget(targetPlayer.transform, slot);
    }


    public void OnPlayerDeath(int deadSlot)
    {
        if (currentSpectatingSlot == deadSlot)
        {
            SetFreeLookMode(true);
        }
    }

    public void SetFreeLookMode(bool freeLook)
    {
        isFollowing = !freeLook;
        currentSpectatingSlot = 0;
        isReturningToTarget = false;

        if (isFollowing && currentTarget == null)
        {
            isFollowing = false;
        }

        if (isFollowing)
        {
            isReturningToTarget = true;
        }

        var personalUI = FindFirstObjectByType<PersonalNotificationManager>();
        if (personalUI != null)
        {
            string message = $"<color=white> 현재 남아있는 플레이어를 관전하고 싶다면 1 ~ 8번 키를 누르세요.</color>";
            personalUI?.PersistentShowPersonalMessage(message);
        }
    }

    
    public void SetFollowTarget(Transform target, int slotNumber)
    {
        if (tag == null) {
            SetFreeLookMode(true);
            return;
        }

        currentTarget = target;
        isFollowing = true;
        currentSpectatingSlot = slotNumber;
        isReturningToTarget = true;

        string colorHex;

        switch (slotNumber)
        {
            case 1:
                colorHex = "red";
                break;
            case 2:
                colorHex = "blue";
                break;
            case 3:
                colorHex = "lime"; // 연두색
                break;
            case 4:
                colorHex = "#800080"; // 보라색
                break;
            case 5:
                colorHex = "orange"; // 주황색
                break;
            case 6:
                colorHex = "#A52A2A"; // 갈색
                break;
            case 7:
                colorHex = "white";
                break;
            case 8:
                colorHex = "yellow";
                break;
            default:
                colorHex = "cyan";
                break;
        }

        string displayName = null;
        PlayerMovement pm = null;

        if (target != null)
        {
            pm = target.GetComponent<PlayerMovement>();
            if (pm == null) pm = target.GetComponentInParent<PlayerMovement>();
            if (pm == null) pm = target.GetComponentInChildren<PlayerMovement>();
        }

        if (pm != null)
        {
            try
            {
                displayName = pm.playerName.Value.ToString();
            }
            catch
            {
                displayName = null;
            }
        }

        // 못 찾았다면 PlayerSpectatorManager를 통해 슬롯->NetworkObject 역추적 시도
        if (string.IsNullOrWhiteSpace(displayName))
        {
            var specMgr = PlayerSpectatorManager.Instance;
            if (specMgr != null)
            {
                var netObj = specMgr.GetPlayerBySlot(slotNumber);
                if (netObj != null)
                {
                    var pm2 = netObj.GetComponent<PlayerMovement>() ?? netObj.GetComponentInChildren<PlayerMovement>();
                    if (pm2 != null)
                    {
                        try
                        {
                            displayName = pm2.playerName.Value.ToString();
                        }
                        catch { displayName = null; }
                    }
                }
            }
        }

        // 최종 폴백
        if (string.IsNullOrWhiteSpace(displayName))
            displayName = $"Player{slotNumber}";

        // 알림 문자열 생성 및 표시
        var personalUI = FindFirstObjectByType<PersonalNotificationManager>();
        if (personalUI != null)
        {
            string message = $"<color={colorHex}>{EscapeStringForRichText(displayName)} 관전 중</color>\n해당 키를 한번 더 누르거나 관전 중인 플레이어가 죽으면 <color=cyan>자유시점</color>으로 전환됩니다.";
            personalUI.PersistentShowPersonalMessage(message);
        }
    }

    private string EscapeStringForRichText(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s.Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
