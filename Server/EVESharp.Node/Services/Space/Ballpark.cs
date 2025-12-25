using System;
using System.Collections.Generic;
using EVESharp.EVE.Data.Inventory.Items;

namespace EVESharp.Node.Services.Space
{
    public class Ballpark
    {
        public int SolarSystemID { get; }
        public int OwnerID       { get; }

        // Public readonly access for snapshot builder
        public IReadOnlyDictionary<int, ItemEntity> Entities => mEntities;

        private readonly Dictionary<int, ItemEntity> mEntities =
            new Dictionary<int, ItemEntity>();

        public Ballpark(int solarSystemID, int ownerID)
        {
            SolarSystemID = solarSystemID;
            OwnerID       = ownerID;
        }

        public void AddEntity(ItemEntity entity)
        {
            if (entity == null)
                throw new ArgumentNullException(nameof(entity));

            mEntities[entity.ID] = entity;
        }

        public bool TryGetEntity(int itemID, out ItemEntity ent)
        {
            return mEntities.TryGetValue(itemID, out ent);
        }
    }
}
