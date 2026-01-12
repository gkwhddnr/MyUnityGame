using UnityEngine;
using Unity.Netcode;
using System.Collections.Generic;
using System.Linq;

public class NetworkChatManager : NetworkBehaviour
{
    [ServerRpc(RequireOwnership = false)]
    public void SubmitMessageServerRpc(string message, ServerRpcParams rpcParams = default)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        ulong senderId = rpcParams.Receive.SenderClientId;
        string full = $"Player{senderId}: {message}";

        // 타겟 결정 (기존 로직 재사용)
        List<ulong> targets;
        if (!DayNightManager.Instance.isNight.Value)
        {
            targets = NetworkManager.Singleton.ConnectedClientsList.Select(c => c.ClientId).ToList();
        }
        else
        {
            // sender의 RoomId 에 해당하는 클라이언트만
            var senderObj = NetworkManager.Singleton.ConnectedClients[senderId].PlayerObject;
            int room = senderObj.GetComponent<PlayerMovement>().RoomId.Value;
            targets = NetworkManager.Singleton.ConnectedClientsList
                .Where(c =>
                {
                    var pm = c.PlayerObject.GetComponent<PlayerMovement>();
                    return pm != null && pm.RoomId.Value == room;
                })
                .Select(c => c.ClientId)
                .ToList();
        }

        if (targets.Count == 0) targets.Add(senderId);

        var clientParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = targets.ToArray() } };

        BroadcastMessageClientRpc(full, clientParams);
    }

    [ClientRpc]
    private void BroadcastMessageClientRpc(string fullMessage, ClientRpcParams rpcParams = default)
    {
        if(string.IsNullOrWhiteSpace(fullMessage)) return;
    }
}
