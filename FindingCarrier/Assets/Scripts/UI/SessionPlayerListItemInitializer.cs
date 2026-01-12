using UnityEngine;
using TMPro;


public class SessionPlayerListItemInitializer : MonoBehaviour
{
    [Tooltip("프리팹 내부의 Player Name 텍스트를 수동으로 연결할 수 있습니다.")]
    public TMP_Text playerNameText;

    [Tooltip("playerNameText를 직접 연결하지 않은 경우, 이 하위 경로로 찾아봅니다 (예: \"Row/Name Container/Player Name\")")]
    public string playerNameChildPath = "Row/Name Container/Player Name";

    // 런타임에서 인스턴스가 생성되면 Start 또는 OnEnable에서 실행됩니다.
    void Start()
    {
        EnsurePlayerNameTextReference();

        if (playerNameText == null) return;

        // 현재 텍스트가 비어있거나 기본 플레이스홀더면 로컬 이름으로 덮어쓴다.
        string current = playerNameText.text ?? string.Empty;
        bool isPlaceholder = string.IsNullOrWhiteSpace(current) || current.Equals("Player") || current.Equals("Unknown") || current.Equals("New Player");

        if (!isPlaceholder)
        {
            // 이미 서버/패키지가 이름을 세팅해줬다면 건드리지 않음
            return;
        }

        // RelaySessionManager에 저장된 로컬 이름을 가져와서 설정
        if (RelaySessionManager.Instance != null)
        {
            string localName = RelaySessionManager.Instance.LocalPlayerName;
            if (!string.IsNullOrEmpty(localName))
            {
                playerNameText.text = localName;
                return;
            }
        }

        // 최종 폴백: PlayerPrefs에 저장된 값이 있으면 사용
        string pref = PlayerPrefs.GetString("LocalPlayerName", string.Empty);
        if (!string.IsNullOrEmpty(pref))
            playerNameText.text = pref;
    }

    void EnsurePlayerNameTextReference()
    {
        if (playerNameText != null) return;

        // 1) 경로로 찾기 시도
        if (!string.IsNullOrEmpty(playerNameChildPath))
        {
            Transform t = transform.Find(playerNameChildPath);
            if (t != null)
            {
                playerNameText = t.GetComponent<TMP_Text>();
                if (playerNameText != null) return;
            }
        }

        // 2) 자식들에서 이름으로 찾기 (비활성 포함)
        var texts = GetComponentsInChildren<TMP_Text>(true);
        foreach (var txt in texts)
        {
            if (txt.gameObject.name.Equals("Player Name") || txt.gameObject.name.Equals("PlayerName"))
            {
                playerNameText = txt;
                return;
            }
        }

        // 3) 그 외 첫번째 TMP_Text(최후 수단)
        if (texts.Length > 0)
            playerNameText = texts[0];
    }
}
