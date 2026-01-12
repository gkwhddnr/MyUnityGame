using Unity.Netcode;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class DayNightManager : NetworkBehaviour
{
    public static DayNightManager Instance;

    [Header("사이클 설정")]
    public int maxDays = 7;

    public int currentDay = 1;
    public NetworkVariable<bool> isNight = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    [Header("이벤트")]
    public UnityEvent onDayStart;
    public UnityEvent onNightStart;
    public UnityEvent onGameOver;

    private void Awake()
    {
        if (Instance != null && Instance != this) Destroy(gameObject);
        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        if (!IsServer) return;

        // 첫 낮 시작
        currentDay = 1;
        // BroadcastDayStart();
    }

    /// <summary> 낮↔밤 토글 </summary>
    public void ToggleDayNight()
    {
        if (!IsServer) return;

        isNight.Value = !isNight.Value;

        if (isNight.Value)
        {
            onNightStart.Invoke();
            BroadcastNightStart();
        }
        else
        {
            currentDay++;
            if (currentDay > maxDays)
            {
                onGameOver.Invoke();
            }
            else
            {
                onDayStart.Invoke();
                BroadcastDayStart();
            }
        }
    }

    private void BroadcastDayStart()
    {
        // 로컬 이벤트
        onDayStart.Invoke();
        // 모든 클라이언트에 메시지
        ShowDayMessageClientRpc(currentDay);
    }

    private void BroadcastNightStart()
    {
        // 로컬 이벤트
        onNightStart.Invoke();
        // 모든 클라이언트에 메시지
        ShowNightMessageClientRpc();
    }

    [ClientRpc]
    void ShowDayMessageClientRpc(int dayCount)
    {
        var globalNotification = GetComponent<GlobalNotificationManager>();
        string msg = $"{dayCount}일차 아침이 되었습니다.";
        globalNotification.ShowGlobalMessageClientRpc(msg);
    }

    [ClientRpc]
    void ShowNightMessageClientRpc()
    {
        var globalNotification = GetComponent<GlobalNotificationManager>();
        string msg = "<color=red>밤이 되었습니다.</color>";
        globalNotification.ShowGlobalMessageClientRpc(msg);
    }
}
