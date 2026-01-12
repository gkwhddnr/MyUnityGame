using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using static UnityEngine.GraphicsBuffer;

public class ZombieController : NetworkBehaviour
{
    [Header("Movement Settings")]
    public float moveSpeed = 2f;
    public float detectionRadius = 2f;
    public float waitTimeMin = 1f;
    public float waitTimeMax = 3f;
    public float attackRange = 1f;
    public float attackDelay = 0.5f;
    public float attackDamage = 50f;

    [Header("Layers")]
    public LayerMask[] obstacleLayers;
    public LayerMask playerLayer;
    public LayerMask[] visionBlockLayers;
    [SerializeField] private LayerMask doorLayer;

    [Header("Audio")]
    [Tooltip("Sound to loop while chasing or player in range")]
    public AudioClip chaseClip;
    [Tooltip("Volume for chase sound")]
    [Range(0, 1f)] public float chaseVolume = 0.7f;

    [Header("Movement Tuning")]
    [Range(0f, 1f)] public float movementSmoothing = 0.85f; 
    public float groundSnapDistance = 0.15f; 
    public float groundCheckRay = 1.2f;

    [Header("Death Effects")]
    [Tooltip("Death effect prefab to spawn when killing a player")]
    public GameObject deathEffectPrefab;

    private AudioSource chaseSource;

    private Vector3 targetSearchPos;
    private Transform targetPlayer;
    private bool isChasing = false;
    private bool isAttacking = false;
    private bool isMoving = false;      // search movement flag

    private Coroutine searchRoutine;
    private DoorVisuals door;

    private Rigidbody rb;
    private Collider col;

    private static readonly List<ZombieController> serverZombies = new List<ZombieController>();
    private Animator animator;


    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        rb = GetComponent<Rigidbody>();
        col = GetComponent<Collider>();
        animator = GetComponent<Animator>();
        doorLayer = LayerMask.GetMask("Door");
        door = FindFirstObjectByType<DoorVisuals>();

        chaseSource = gameObject.AddComponent<AudioSource>();
        chaseSource.clip = chaseClip;
        chaseSource.loop = true;
        chaseSource.playOnAwake = false;
        chaseSource.volume = chaseVolume;

        if (rb != null)
        {
            rb.isKinematic = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }


        if (IsServer)
        {
            if(!serverZombies.Contains(this)) serverZombies.Add(this);

            NetworkObject.ChangeOwnership(NetworkManager.ServerClientId);
            StartSearch();
        }
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();
        // 네트워크에서 내려갈 때 레지스트리에서 제거
        if (IsServer)
            serverZombies.Remove(this);

        if (chaseSource != null && chaseSource.isPlaying)
            chaseSource.Stop();
    }

    public override void OnDestroy()
    {
        // 안전 장치: 오브젝트가 완전히 파괴될 때 제거
        serverZombies.Remove(this);
    }

    private void FixedUpdate()
    {
        if (!IsServer || isAttacking) return;

        DetectPlayer();

        // 1) 항상 가장 가까운 플레이어 찾기 (DetectPlayer에서 targetPlayer 셋업)
        if (isChasing)
        {
            if (!chaseSource.isPlaying) chaseSource.Play();
        }
        else
        {
            if (chaseSource.isPlaying) chaseSource.Stop();
        }

        UpdateAnimatorIsWalking(IsWalkingState());

        // 2) chase vs search
        if (isChasing)
            ChasePlayer();
        else
            SearchMovement();

        PushOutFromDoor();

        EnsureNotFloating();

        // 서버가 위치를 계산 중이므로 위치 브로드캐스트 (간단하게)
        if (rb != null)
            SendPositionClientRpc(rb.position);
    }

    private bool IsWalkingState()
    {
        // 달리기/탐색 중이면 true, 그 외는 false
        return (isChasing || isMoving) && !isAttacking;
    }

    private void UpdateAnimatorIsWalking(bool walking)
    {
        if (IsServer)
        {
            var clientParams = new ClientRpcParams(); // broadcast
            SetAnimatorIsWalkingClientRpc(walking, clientParams);
        }
    }

    [ClientRpc]
    private void SetAnimatorIsWalkingClientRpc(bool walking, ClientRpcParams rpcParams = default)
    {
        if (animator != null)
        {
            animator.SetBool("IsWalking", walking);
        }
    }

    public static IReadOnlyList<ZombieController> GetServerZombies()
    {
        return serverZombies;
    }

    [ServerRpc(RequireOwnership = false)]
    public void SendPersonalMessageServerRpc(int targetClientId, float health)
    {
        var message = health > 0
            ? $"공격받았습니다. 남은 체력 : <color=yellow>{health}</color>"
            : $"공격받았습니다. 남은 체력 : <color=red>{health}</color>";

        var clientParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams
            {
                TargetClientIds = new ulong[] { (ulong)targetClientId } // 해당 클라이언트만
            }
        };

        SendPlayerMessageClientRpc(message, clientParams);
        if(health == 0)
        {
            message = "당신은 <color=red>죽었습니다. </color>\n 관전하고 싶은 <color=green>플레이어</color>가 있다면\n <color=cyan>번호키</color>를 눌러주세요.";
            SendPlayerMessageClientRpc(message, clientParams);
            ShowDeathNotificationClientRpc(clientParams);
        }
    }

    [ClientRpc]
    private void ShowDeathNotificationClientRpc(ClientRpcParams rpcParams = default)
    {
        var deathPersonalUI = FindFirstObjectByType<DeathNotificationUI>(FindObjectsInactive.Include);
        deathPersonalUI?.ShowDeathNotification();
    }

    [ClientRpc]
    private void SendPlayerMessageClientRpc(string message, ClientRpcParams rpcParams = default)
    {
        PersonalNotificationManager.Instance?.ShowPersonalMessage(message);
    }

    [ClientRpc]
    private void SendPositionClientRpc(Vector3 pos)
    {
        if (IsServer) return; // 서버는 이미 직접 이동 중이므로 무시

        transform.position = pos;
    }

    private void MoveWithVelocity(Vector3 direction)
    {
        if (rb == null) return;

        // 방향 정규화 (XZ)
        Vector3 dir = direction;
        dir.y = 0f;
        if (dir.sqrMagnitude > 0.0001f) dir.Normalize();
        Vector3 desiredHorizontal = dir * moveSpeed;

        // 현재 Y 속도 보존
#if UNITY_2023_2_OR_NEWER
        Vector3 currentVel = rb.linearVelocity;
#else
        Vector3 currentVel = rb.velocity;
#endif
        float currentY = currentVel.y;

        Vector3 targetVel = new Vector3(desiredHorizontal.x, currentY, desiredHorizontal.z);

        // smoothing: 0..1 (작을수록 즉시 변경)
        float smooth = Mathf.Clamp01(1f - movementSmoothing);
        // we want a lerp factor that moves most of the way when smoothing is small.
        // Using movementSmoothing as "inertia": higher means more inertia (slower change)
        Vector3 newVel = Vector3.Lerp(currentVel, targetVel, Mathf.Clamp01(1f - movementSmoothing));

#if UNITY_2023_2_OR_NEWER
        rb.linearVelocity = newVel;
        rb.angularVelocity = Vector3.zero;
#else
        rb.velocity = newVel;
        rb.angularVelocity = Vector3.zero;
#endif
    }

    private void EnsureNotFloating()
    {
        if (rb == null) return;

        // 반드시 non-kinematic 상태여야 snap 동작을 할 수 있음
        if (rb.isKinematic) return;

        // 아래로 raycast 해서 땅이 조금만 아래에 있으면 보정
        Vector3 origin = transform.position + Vector3.up * 0.1f;
        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit, groundCheckRay))
        {
            float distanceToGround = hit.distance - 0.1f; // compensate origin offset
            if (distanceToGround > 0.01f && distanceToGround < groundSnapDistance)
            {
                Vector3 pos = rb.position;
                pos.y = hit.point.y + 0.01f;
#if UNITY_2023_2_OR_NEWER
                rb.linearVelocity = new Vector3(rb.linearVelocity.x, rb.linearVelocity.y, rb.linearVelocity.z);
#endif
                rb.MovePosition(pos);
            }
        }
    }

    // Search until player found
    private void StartSearch()
    {
        if (searchRoutine != null) StopCoroutine(searchRoutine);
        searchRoutine = StartCoroutine(SearchRoutine());
    }

    private void SearchMovement()
    {
        if (searchRoutine == null) StartSearch();

        if (isMoving)
        {
            Vector3 dir = (targetSearchPos - transform.position);
            dir.y = 0f;
            if (dir.sqrMagnitude > 0.001f)
            {
                dir.Normalize();
                Quaternion targetRot = Quaternion.LookRotation(dir);
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.fixedDeltaTime * 10f);
            }

            MoveWithVelocity(dir);
        }
        else
        {
            if (rb == null) return;

            // idle: 수평 속도만 0으로 (Y 보존)
#if UNITY_2023_2_OR_NEWER
            Vector3 lv = rb.linearVelocity;
            rb.linearVelocity = new Vector3(0f, lv.y, 0f);
            rb.angularVelocity = Vector3.zero;
#else
            Vector3 lv = rb.velocity;
            rb.velocity = new Vector3(0f, lv.y, 0f);
            rb.angularVelocity = Vector3.zero;
#endif
        }
    }

    private IEnumerator SearchRoutine()
    {
        while (!isChasing)
        {
            Vector2 r = Random.insideUnitCircle * detectionRadius;
            Vector3 cand = transform.position + new Vector3(r.x, 0, r.y);
            if (!CheckObstacle(cand))
                targetSearchPos = cand;

            // start moving
            isMoving = true;
            float moveDuration = Random.Range(waitTimeMin, waitTimeMax);
            float timer = 0f;
            while (timer < moveDuration && !isChasing)
            {
                timer += Time.deltaTime;
                yield return null;
            }

            // stop moving, idle
            isMoving = false;
            float idleDuration = Random.Range(waitTimeMin, waitTimeMax);
            timer = 0f;
            while (timer < idleDuration && !isChasing)
            {
                timer += Time.deltaTime;
                yield return null;
            }
        }
        searchRoutine = null;
    }

    private void DetectPlayer()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, detectionRadius, playerLayer);
        float minDist = float.MaxValue;
        Transform best = null;

        int visionMask = GetCombinedLayerMask(visionBlockLayers);
        if (door != null && (!door.GetIsOpen() || door.GetIsBusy()))
            visionMask |= doorLayer.value;

        foreach (var h in hits)
        {
            if (h == null) continue;

            // 1) 충돌체에서 OwnerClientId 추출
            var hitNetObj = h.GetComponentInParent<NetworkObject>() ?? h.GetComponent<NetworkObject>();
            if (hitNetObj == null) continue;
            ulong owner = hitNetObj.OwnerClientId;

            // 2) 해당 소유자의 실제 캐릭터 NetworkObject 찾기 (컨테이너/캐릭터 구조가 바뀐 경우에도 안전)
            var charNetObj = GetCharacterNetworkObjectByOwner(owner);
            if (charNetObj == null) continue;

            // 3) 숨음 체크: 서버측 Hideable 딕셔너리에 등록되어 있으면 무시
            if (HideableObject.HideableByOwner != null && HideableObject.HideableByOwner.ContainsKey(owner))
                continue;

            // 4) 생존/이동 가능 체크 (서버 기준 NetworkVariable 사용)
            var health = charNetObj.GetComponent<PlayerHealth>();
            if (health != null && health.Health.Value <= 0f) continue;

            var pm = charNetObj.GetComponent<PlayerMovement>();
            if (pm != null)
            {
                // 서버 권한으로 CanMoveVar 확인 (Owner-local bool 대신 네트워크 변수 사용)
                if (!pm.CanMoveVar.Value) continue;
            }

            // 5) 거리/시야 검사 (캐릭터 Transform 기준)
            Vector3 targetPos = charNetObj.transform.position;
            float dist = Vector3.Distance(transform.position, targetPos);
            Vector3 dir = (targetPos - transform.position).normalized;

            if (Physics.Raycast(transform.position, dir, out var rh, dist, visionMask))
                continue;

            if (dist < minDist)
            {
                minDist = dist;
                best = charNetObj.transform;
            }
        }

        if (best != null)
        {
            // 가장 가까운 실제 캐릭터를 타겟으로 설정
            targetPlayer = best;
            isChasing = true;
            if (searchRoutine != null) StopCoroutine(searchRoutine);
            isMoving = false;
        }
        else
        {
            // 찾지 못하면 추적 종료(서치 재개)
            if (isChasing)
            {
                isChasing = false;
            }
        }
    }

    private NetworkObject GetCharacterNetworkObjectByOwner(ulong ownerClientId)
    {
        if (NetworkManager.Singleton == null) return null;

        // 1) 빠른 폴백: SpawnedObjects 탐색 (캐릭터에 PlayerHealth가 붙어있거나 PlayerMovement.IsCharacterInstance()인 객체를 우선)
        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var netObj = kv.Value;
            if (netObj == null) continue;
            if (netObj.OwnerClientId != ownerClientId) continue;

            if (netObj.GetComponent<PlayerHealth>() != null) return netObj;

            var pm = netObj.GetComponent<PlayerMovement>();
            if (pm != null && pm.IsCharacterInstance()) return netObj;
        }

        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var netObj = kv.Value;
            if (netObj == null) continue;
            if (netObj.OwnerClientId == ownerClientId) return netObj;
        }

        return null;
    }

    private void ChasePlayer()
    {
        if (targetPlayer == null)
        {
            isChasing = false;
            return;
        }

        Vector3 direction = (targetPlayer.position - transform.position);
        direction.y = 0f;
        float distToPlayer = Vector3.Distance(transform.position, targetPlayer.position);

        int visionMask = GetCombinedLayerMask(visionBlockLayers) | doorLayer.value;
        Vector3 dirNormal = direction.normalized;
        if (Physics.Raycast(transform.position, dirNormal, out var hit, distToPlayer, visionMask))
        {
            isChasing = false;
            targetPlayer = null;
            StartSearch();
            return;
        }

        if (distToPlayer <= attackRange)
        {
            StartCoroutine(AttackDelayCoroutine(targetPlayer));
            return;
        }

        // Move using velocity (preserve Y)
        MoveWithVelocity(dirNormal);

        if (dirNormal.sqrMagnitude > 0.001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(dirNormal);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.fixedDeltaTime * 10f);
        }
    }

    private IEnumerator AttackDelayCoroutine(Transform target)
    {
        if (!IsServer)
            yield break; // 서버에서만 처리

        if (isAttacking) yield break;
        isAttacking = true;
        isChasing = false;

        while (true)
        {
            // 대상 유효성/거리 확인
            if (target == null) break;
            float dist = Vector3.Distance(transform.position, target.position);
            if (dist > attackRange) break;
            int attackIndex = Random.Range(0, 2);

            if (animator != null)
            {
                // 현재 애니메이션 길이 가져오기
                float animLength = animator.runtimeAnimatorController.animationClips[attackIndex].length;
                animator.speed = animLength / attackDelay; // attackDelay 안에 애니 끝나도록 속도 조절
            }

            PlayAttackAnimationClientRpc(attackIndex);

            float timer = 0f;
            while (timer < attackDelay)
            {
                // 중간에 대상이 사라지거나 범위 이탈하면 공격 루프 종료
                if (target == null) break;
                if (Vector3.Distance(transform.position, target.position) > attackRange) break;

                timer += Time.deltaTime;
                yield return null;
            }

            // 확인 후 데미지 적용 (서버 권한)
            if (target != null && Vector3.Distance(transform.position, target.position) <= attackRange)
            {
                var ph = target.GetComponent<PlayerHealth>();
                if (ph != null)
                {
                    // 플레이어가 죽을지 미리 확인
                    bool willDie = (ph.Health.Value - attackDamage) <= 0f;
                    Vector3 deathPosition = target.position; // 사망 위치 저장

                    ph.ApplyDamage(attackDamage);
                    SendPersonalMessageServerRpc((int)ph.OwnerClientId, ph.Health.Value);

                    // 플레이어가 죽었다면 데스 이펙트 생성
                    if (willDie)
                    {
                        PlayDeathEffectClientRpc(deathPosition);
                    }
                }
            }
            else
            {
                break;
            }
        }

        // 공격 끝
        if (animator != null) animator.speed = 1f;
        isAttacking = false;
        isChasing = true;
        // 재탐색 시작
        StartSearch();
    }

    [ClientRpc]
    private void PlayAttackAnimationClientRpc(int attackIndex, ClientRpcParams rpcParams = default)
    {
        if (animator == null) return;

        // 방법1: AttackIndex + Attack Trigger 사용
        animator.SetInteger("AttackIndex", attackIndex);
        animator.SetTrigger("Attack");
    }

    [ClientRpc]
    private void PlayDeathEffectClientRpc(Vector3 deathPosition, ClientRpcParams rpcParams = default)
    {
        if (deathEffectPrefab == null) return;

        // 이펙트는 네트워크 오브젝트가 아닌 단순 비주얼 프리팹이어야 합니다.
        GameObject fx = Instantiate(deathEffectPrefab, deathPosition, Quaternion.identity);

        // ParticleSystem이 포함되어 있으면 재생 시간에 맞춰 자동 삭제
        var ps = fx.GetComponentInChildren<ParticleSystem>();
        if (ps != null)
        {
            var main = ps.main;
            float maxLifetime = 1f;
#if UNITY_2023_2_OR_NEWER
            maxLifetime = main.startLifetime.constantMax;
#else
            // fallback: constant (대부분 단일값인 경우)
            maxLifetime = main.startLifetime.constant;
#endif
            float destroyAfter = main.duration + maxLifetime + 0.1f;
            Destroy(fx, destroyAfter);
        }
        else
        {
            // ParticleSystem이 없으면 기본 삭제 시간
            Destroy(fx, 3f);
        }
    }

    private void PushOutFromDoor()
    {
        if (door == null || col == null) return;
        var doorCol = door.GetComponent<Collider>();
        float radius = col.bounds.extents.magnitude;
        Collider[] hits = Physics.OverlapSphere(transform.position, radius, doorLayer, QueryTriggerInteraction.Ignore);
        foreach (var h in hits)
        {
            if (Physics.ComputePenetration(
                doorCol, doorCol.transform.position, doorCol.transform.rotation,
                col, transform.position, transform.rotation,
                out Vector3 dir, out float dist))
            {
                Vector3 push = new Vector3(dir.x, 0, dir.z) * dist;
                rb.MovePosition(rb.position + push);
            }
        }
    }

    private int GetCombinedLayerMask(LayerMask[] masks)
    {
        int m = 0;
        foreach (var mask in masks) m |= mask.value;
        return m;
    }

    private bool CheckObstacle(Vector3 pos)
    {
        foreach (var mask in obstacleLayers)
        {
            var cols = Physics.OverlapSphere(pos, 0.15f, mask);
            foreach (var c in cols)
            {
                var dv = c.GetComponentInParent<DoorVisuals>();
                if (dv != null && (!dv.GetIsOpen() || dv.GetIsBusy())) return true;
                if (!c.isTrigger) return true;
            }
        }
        return false;
    }

    public void ResetStuck()
    {
        if (!IsServer) return;
        isChasing = false;
        targetPlayer = null;
        StartSearch();
    }

    public bool HasTarget(Transform t)
    {
        return targetPlayer == t;
    }

    // 타겟 클리어하고 탐색(검색) 상태로 되돌리는 함수
    public void ClearTargetAndResume()
    {
        if (!IsServer) return; // 서버에서만 처리

        targetPlayer = null;
        isChasing = false;
        isAttacking = false;

        // 코루틴 정리
        if (searchRoutine != null)
        {
            StopCoroutine(searchRoutine);
            searchRoutine = null;
        }

        StartSearch();
    }
}
