#if UNITY_NETCODE || ENABLE_MONO || UNITY_2021_1_OR_NEWER
using Unity.Netcode;
using UnityEngine;

public class PlayerLocalUI : NetworkBehaviour
{
    [Tooltip("UIScreenTransitionManager가 포함된 UI Canvas Prefab")]
    public GameObject uiPrefab;

    private GameObject uiInstance;

    public override void OnNetworkSpawn()
    {
        base.OnNetworkSpawn();

        if (IsLocalPlayer)
        {
            if (uiPrefab == null) return;

            uiInstance = Instantiate(uiPrefab);

            // 안전: Canvas가 있으면 Screen Space - Overlay로 강제 설정 (로컬 뷰에 잘 뜨게)
            var canvas = uiInstance.GetComponentInChildren<Canvas>(true);
            if (canvas != null)
            {
                // overlay 모드로 강제 (빌드/에디터 상황에 따라 필요하면 주석 해제)
                canvas.renderMode = RenderMode.ScreenSpaceOverlay;
                canvas.overrideSorting = true;
                canvas.sortingOrder = 1000;
            }

            // 필요하면 씬 전환시 유지
            // DontDestroyOnLoad(uiInstance);
        }
    }

    public override void OnNetworkDespawn()
    {
        base.OnNetworkDespawn();

        if (uiInstance != null)
        {
            Destroy(uiInstance);
            uiInstance = null;
        }
    }
}
#else
// Netcode 패키지가 없을 경우 경고용 더미 클래스 (컴파일 오류 방지)
public class PlayerLocalUI : MonoBehaviour
{
    public GameObject uiPrefab;
}
#endif