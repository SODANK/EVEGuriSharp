using System;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Sessions;
using EVESharp.Types;
using EVESharp.Types.Collections;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Network.Services.Validators;


namespace EVESharp.Node.Services.Inventory
{
    [ConcreteService("shipSvc")]
    public class shipSvc : ClientBoundService
    {
        public override AccessLevel AccessLevel => AccessLevel.None;

        // Global constructor
        public shipSvc(IBoundServiceManager manager)
            : base(manager)
        {
            Console.WriteLine("[shipSvc] Global service constructed");
        }

        // Bound constructor
        public shipSvc(IBoundServiceManager manager, Session session, int shipID)
            : base(manager, session, shipID)
        {
            Console.WriteLine(
                $"[shipSvc] Bound instance created for char={session.CharacterID}, shipID={shipID}");
        }

        // Tell client this node owns all ships
        protected override long MachoResolveObject(ServiceCall call, ServiceBindParams parameters)
        {
            Console.WriteLine("[shipSvc] MachoResolveObject invoked");
            return BoundServiceManager.MachoNet.NodeID;
        }

        // Create a bound ship instance
        protected override BoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams)
        {
            Console.WriteLine($"[shipSvc] CreateBoundInstance for shipID={bindParams.ObjectID}");

            var instance = new shipSvc(BoundServiceManager, call.Session, bindParams.ObjectID);
            return instance;
        }

        // -------------------------
        // REQUIRED BY CLIENT
        // Called during undock flow
        // -------------------------
        public PyDataType GetStateForShip(ServiceCall call)
        {
            Console.WriteLine("[shipSvc] GetStateForShip() called");

            int shipID = call.Session.ShipID ?? 0;
            int ownerID = call.Session.CharacterID;

            // Minimal slimItem
            var slimItem = new PyDictionary
            {
                ["itemID"]  = new PyInteger(shipID),
                ["typeID"]  = new PyInteger(GetShipTypeID(call)),
                ["ownerID"] = new PyInteger(ownerID),
                ["flag"]    = new PyInteger(0),
                ["quantity"] = new PyInteger(1)
            };

            // The client expects a tuple: (slimItem, damageState)
            var stateTuple = new PyTuple(2)
            {
                [0] = slimItem,
                [1] = new PyNone() // no damage state yet
            };

            return stateTuple;
        }

        // Apoc client often calls this too
        public PyDataType GetDamageState(ServiceCall call)
        {
            Console.WriteLine("[shipSvc] GetDamageState() called");
            return new PyNone();
        }

        // Safety fallback
        public PyDataType GetModules(ServiceCall call)
        {
            Console.WriteLine("[shipSvc] GetModules() called");
            return new PyList(); 
        }

        public PyDataType GetMultiLevels(ServiceCall call)
        {
            Console.WriteLine("[shipSvc] GetMultiLevels() called");
            return new PyList();
        }

        // Let ballpark take over universe entry
        public PyDataType EnterSpace(ServiceCall call)
        {
            Console.WriteLine("[shipSvc] EnterSpace() called - passing through");

            // Client expects a PyNone
            return new PyNone();
        }

        protected override void OnClientDisconnected()
        {
            Console.WriteLine("[shipSvc] Client disconnected from ship bound object.");
        }
private int GetShipTypeID(ServiceCall call)
{
    try
    {
        // Many Apoc builds use this hidden session variable
        var typeID = call.Session["shipTypeID"] as PyInteger;
        if (typeID != null)
            return (int)typeID.Value;
    }
    catch 
    {
        // ignore
    }

    return 0;
}

  }
}
