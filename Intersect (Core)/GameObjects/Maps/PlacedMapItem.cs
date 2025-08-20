using System;
using Intersect.Network.Packets.Server;

namespace Intersect.GameObjects.Maps
{
    public class PlacedMapItem
    {
        public Guid ItemId { get; set; }
        public Guid? BagId { get; set; }
        public int Quantity { get; set; }
        public int X { get; set; }
        public int Y { get; set; }
        public ItemProperties Properties { get; set; } = new ItemProperties();
    }
}
