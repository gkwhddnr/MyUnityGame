using System.Collections.Generic;
using Unity.Netcode;

public struct PlayerSlotData : INetworkSerializable
{
    public int SlotNumber;
    public ulong ClientId;
    public ulong PlayerNetId; // NetworkObject.NetworkObjectId

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref SlotNumber);
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref PlayerNetId);
    }
}

public struct PlayerSlotDataList : INetworkSerializable
{
    public List<PlayerSlotData> PlayerSlots;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        int count = PlayerSlots != null ? PlayerSlots.Count : 0;
        serializer.SerializeValue(ref count);
        if (serializer.IsReader)
        {
            PlayerSlots = new List<PlayerSlotData>(count);
            for (int i = 0; i < count; i++)
            {
                PlayerSlotData slotData = new PlayerSlotData();
                serializer.SerializeValue(ref slotData);
                PlayerSlots.Add(slotData);
            }
        }
        else
        {
            for (int i = 0; i < count; i++)
            {
                PlayerSlotData slotData = PlayerSlots[i];
                serializer.SerializeValue(ref slotData);
            }
        }
    }
}
