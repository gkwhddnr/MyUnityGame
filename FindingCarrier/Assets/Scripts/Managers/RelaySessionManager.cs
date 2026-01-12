using UnityEngine;

public class RelaySessionManager : MonoBehaviour
{
    public static RelaySessionManager Instance;

    public string CurrentSessionId { get; private set; }

    // 추가: 로컬 플레이어 이름 저장
    public string LocalPlayerName { get; private set; }

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject); // 씬 전환 시 유지
        }
        else
        {
            Destroy(gameObject);
        }
    }

    public void SetSessionId(string sessionId)
    {
        CurrentSessionId = sessionId;
    }

    public void SetLocalPlayerName(string name)
    {
        LocalPlayerName = name ?? string.Empty;
        // (선택) 영구 저장
        PlayerPrefs.SetString("LocalPlayerName", LocalPlayerName);
    }

    // (선택) 애플 시작 시 저장된 이름 복구
    public void LoadLocalPlayerNameFromPrefs()
    {
        LocalPlayerName = PlayerPrefs.GetString("LocalPlayerName", string.Empty);
    }
}
