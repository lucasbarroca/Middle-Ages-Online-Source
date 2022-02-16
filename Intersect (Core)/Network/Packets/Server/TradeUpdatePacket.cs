using MessagePack;
using System;

namespace Intersect.Network.Packets.Server
{
    [MessagePackObject]
    public class TradeUpdatePacket : InventoryUpdatePacket
    {
        //Parameterless Constructor for MessagePack
        public TradeUpdatePacket() : base(0, Guid.Empty, 0, null, new int[(int)Enums.Stats.StatCount], 0, 0)
        {
        }

        public TradeUpdatePacket(Guid traderId, int slot, Guid id, int quantity, Guid? bagId, int[] statBuffs, long exp, int level) : base(
            slot, id, quantity, bagId, statBuffs, exp, level
        )
        {
            TraderId = traderId;
        }

        [Key(8)]
        public Guid TraderId { get; set; }

    }

}
