using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// 세션을 떠나거나 강퇴당했을 때 호출할 수 있는
/// 완전 커스텀 "나가기/강퇴 복귀" 버튼 컴포넌트.
/// </summary>
[RequireComponent(typeof(Button))]
public class CustomLeaveButton : MonoBehaviour
{
    [Tooltip("킥 당했을 때 돌아갈 화면 인덱스")]
    public int exitScreenIndex = 2;

    [Tooltip("UIScreenTransitionManager 인스턴스")]
    public UIScreenTransitionManager uiManager;

    Button _btn;
    ulong _localId;

    void Awake()
    {
        _btn = GetComponent<Button>();
        _btn.onClick.AddListener(OnExitClicked);
        _btn.gameObject.SetActive(false);

        if (NetworkManager.Singleton != null)
            _localId = NetworkManager.Singleton.LocalClientId;
    }

    void OnEnable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback += OnClientDisconnect;
    }

    void OnDisable()
    {
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.OnClientDisconnectCallback -= OnClientDisconnect;
    }

    private void OnClientDisconnect(ulong clientId)
    {
        // 로컬 클라이언트가 끊겼다면(=킥 당했거나 직접 연결 해제)
        if (clientId == _localId)
        {
            // 화면 전환 허용
            uiManager.EnableTransition();
            uiManager.OnTransitionButtonClicked(exitScreenIndex);

            // 버튼 보이기
            _btn.gameObject.SetActive(true);
        }
    }

    private void OnExitClicked()
    {
        // 네트워크 완전 종료
        if (NetworkManager.Singleton != null)
            NetworkManager.Singleton.Shutdown();

        // 다시 같은 화면(혹은 원하는 화면)으로 전환
        uiManager.EnableTransition();
        uiManager.OnTransitionButtonClicked(exitScreenIndex);

        _btn.gameObject.SetActive(false);
    }
}
