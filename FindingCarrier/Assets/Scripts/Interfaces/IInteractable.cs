using Unity.Netcode;

public interface IInteractable
{
    void InteractServerRpc(ulong clientId, ServerRpcParams rpcParams = default);
}
