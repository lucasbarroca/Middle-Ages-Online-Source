using MessagePack;

namespace Intersect.Network.Packets.Client;

[MessagePackObject]
public class PlaceItemPacket : IntersectPacket
{
    //Parameterless Constructor for MessagePack
    public PlaceItemPacket()
    {
    }

    public PlaceItemPacket(int slot)
    {
        Slot = slot;
    }

    [Key(0)]
    public int Slot { get; set; }
}
