using Unity.Netcode;
using UnityEngine;

public class HidingSpot : NetworkBehaviour
{
    private NetworkVariable<bool> isHiding = new NetworkVariable<bool>(false);
    private NetworkVariable<ulong> playerId = new NetworkVariable<ulong>();

    public bool IsInUse()
    {
        return isHiding.Value;
    }

    public ulong GetCurrentUserId()
    {
        return playerId.Value;
    }

    [ServerRpc(RequireOwnership = false)]
    public void SetHidingStateServerRpc(bool hiding, ulong playerId)
    {
        isHiding.Value = hiding;
        this.playerId.Value = playerId;
    }



    [ClientRpc]
    public void ShowMessageClientRpc(ulong targetPlayerId, string message)
    {
        if (NetworkManager.Singleton.LocalClientId == targetPlayerId)
        {
            Debug.Log(message);
        }
    }
}
