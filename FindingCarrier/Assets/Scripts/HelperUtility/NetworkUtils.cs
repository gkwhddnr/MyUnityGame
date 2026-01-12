using System.Linq;
using Unity.Netcode;
using UnityEngine;

public static class NetworkUtils
{
    /// <summary>
    /// ownerClientId로 소유된 "실제 캐릭터" NetworkObject를 찾는다.
    /// 우선 PlayerHealth, 그 다음 PlayerMovement(IsCharacterInstance) 순으로 찾음.
    /// </summary>
    public static NetworkObject FindCharacterNetworkObjectForClient(ulong ownerClientId)
    {
        if (NetworkManager.Singleton == null) return null;

        foreach (var kv in NetworkManager.Singleton.SpawnManager.SpawnedObjects)
        {
            var no = kv.Value;
            if (no == null) continue;
            if (no.OwnerClientId != ownerClientId) continue;

            // 우선 PlayerHealth(캐릭터) 체크
            if (no.TryGetComponent<PlayerHealth>(out _)) return no;

            // 혹은 PlayerMovement & IsCharacterInstance
            if (no.TryGetComponent<PlayerMovement>(out var pm) && pm.IsCharacterInstance())
                return no;
        }

        // fallback: 일부 케이스에서는 컨테이너의 자식으로 캐릭터가 붙어있을 수 있음
        // 전 범위 씬 검색 (비활성 포함)
#if UNITY_2023_2_OR_NEWER
        var allPMs = UnityEngine.Object.FindObjectsByType<PlayerMovement>(FindObjectsInactive.Include, FindObjectsSortMode.None);
#else
        var allObjs = Resources.FindObjectsOfTypeAll<PlayerMovement>();
        var allPMs = allObjs.Where(p => p != null && p.gameObject != null && p.gameObject.scene.IsValid() && p.gameObject.scene.isLoaded).ToArray();
#endif
        foreach (var pm in allPMs)
        {
            if (pm == null) continue;
            if (pm.OwnerClientId == ownerClientId && pm.IsCharacterInstance())
                return pm.GetComponentInParent<NetworkObject>() ?? pm.GetComponent<NetworkObject>();
        }

        return null;
    }
}
