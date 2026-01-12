using System.Collections;
using Unity.Netcode;
using UnityEngine;

public class PlayerInteraction : NetworkBehaviour
{
    public static PlayerInteraction localPlayerInteraction;

    public float interactRange = 1f;
    public KeyCode interactKey = KeyCode.E;

    private bool canInteract = true;
    public bool isInteracting = false;

    private PersonalNotificationManager personalUI;


    public override void OnNetworkSpawn()
    {
        if (IsOwner)
        {
            personalUI = FindFirstObjectByType<PersonalNotificationManager>(FindObjectsInactive.Include);
            if (personalUI != null) personalUI.gameObject.SetActive(true);
        }
    }

    private void Awake()
    {
        if (IsOwner) localPlayerInteraction = this;
    }

    void Update()
    {
        if (!IsOwner || !canInteract || DayNightManager.Instance.isNight.Value) return;

        if (Input.GetKeyDown(interactKey))
        {
            Collider[] hits = Physics.OverlapSphere(transform.position, interactRange);

            IInteractable nearest = null;
            float closestDistance = Mathf.Infinity;

            foreach (var hit in hits)
            {
                var interactable = hit.GetComponent<IInteractable>();
                if (interactable != null)
                {
                    float dist = Vector3.Distance(transform.position, hit.transform.position);
                    if (dist < closestDistance)
                    {
                        closestDistance = dist;
                        nearest = interactable;
                    }
                }
            }

            if (nearest != null)
            {
                switch (nearest)
                {
                    case DoorVisuals door:
                        door.InteractServerRpc(NetworkManager.Singleton.LocalClientId);
                        StartCoroutine(DoorInteractionCooldown()); // 문 상호작용 쿨타임
                        break;

                        
                    case HideableObject hideableObject:
                        hideableObject.InteractServerRpc(NetworkManager.Singleton.LocalClientId);
                        StartCoroutine(HidingCooldown()); // 숨는 오브젝트 상호작용 쿨타임
                        break;

                    default:
                        nearest.InteractServerRpc(NetworkManager.Singleton.LocalClientId);
                        Debug.Log("기타 오브젝트와 상호작용");
                        StartCoroutine(InteractionCooldown());
                        break;
                }
                isInteracting = true;
                canInteract = false;
            }
        }
    }

    // 공통 인터랙션 쿨타임 (기타용)
    private IEnumerator InteractionCooldown()
    {
        yield return new WaitForSeconds(1f);
        canInteract = true;
        isInteracting = false;
    }

    // 문 전용 쿨타임
    private IEnumerator DoorInteractionCooldown()
    {
        yield return new WaitForSeconds(1f);
        canInteract = true;
        isInteracting = false;
    }

    // 숨는 오브젝트 전용 쿨타임
    private IEnumerator HidingCooldown()
    {
        yield return new WaitForSeconds(1f);
        canInteract = true;
        isInteracting = false;
    }

    public bool IsInteracting()
    {
        return isInteracting;
    }

    public PersonalNotificationManager GetPersonalUI()
    {
        return personalUI;
    }
}
