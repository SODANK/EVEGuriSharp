using System;
using System.IO;
using System.Collections.Generic;
using EVESharp.Types;
using EVESharp.Database;
using EVESharp.Database.Extensions;
using EVESharp.Database.Inventory;
using EVESharp.Database.Inventory.Groups;
using EVESharp.Database.Inventory.Types;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Data.Inventory.Items.Types;
using EVESharp.EVE.Dogma;
using EVESharp.EVE.Exceptions;
using EVESharp.EVE.Exceptions.ship;
using EVESharp.EVE.Network.Services;
using EVESharp.Node.Services.Navigation;
using EVESharp.EVE.Network.Services.Validators;
using EVESharp.EVE.Notifications;
using EVESharp.EVE.Notifications.Inventory;
using EVESharp.EVE.Sessions;
using EVESharp.Types.Collections;
using EVESharp.Destiny;


namespace EVESharp.Node.Services.Inventory;

[MustBeCharacter]
[ConcreteService("ship")]
public class ship : ClientBoundService
{
    public override AccessLevel AccessLevel => AccessLevel.None;
    private ItemEntity Location { get; }
    private IItems Items { get; }
    private ITypes Types => this.Items.Types;
    private ISolarSystems SolarSystems { get; }
    private ISessionManager SessionManager { get; }
    private IDogmaNotifications DogmaNotifications { get; }
    private IDatabase Database { get; }
    private IDogmaItems DogmaItems { get; }
    private INotificationSender NotificationSender { get; }

    public ship(
        IItems items, IBoundServiceManager manager, ISessionManager sessionManager, IDogmaNotifications dogmaNotifications,
        IDatabase database, ISolarSystems solarSystems, IDogmaItems dogmaItems, INotificationSender notificationSender
    ) : base(manager)
    {
        Console.WriteLine("[DEBUG] Node.Services.Inventory.ship (global service) constructed");
        Items = items;
        SessionManager = sessionManager;
        DogmaNotifications = dogmaNotifications;
        Database = database;
        SolarSystems = solarSystems;
        DogmaItems = dogmaItems;
        NotificationSender = notificationSender;
    }

    protected ship(
        ItemEntity location, IItems items, IBoundServiceManager manager, ISessionManager sessionManager,
        IDogmaNotifications dogmaNotifications, Session session, ISolarSystems solarSystems, IDogmaItems dogmaItems,
        INotificationSender notificationSender
    ) : base(manager, session, location.ID)
    {
        Console.WriteLine("[DEBUG] Node.Services.Inventory.ship (bound instance) constructed for objectID=" + location.ID);
        Location = location;
        Items = items;
        SessionManager = sessionManager;
        DogmaNotifications = dogmaNotifications;
        SolarSystems = solarSystems;
        DogmaItems = dogmaItems;
        NotificationSender = notificationSender;
    }

    public PyInteger LeaveShip(ServiceCall call)
    {
        int callerCharacterID = call.Session.CharacterID;

        Character character = this.Items.GetItem<Character>(callerCharacterID);
        ItemInventory capsule = DogmaItems.CreateItem<ItemInventory>(
            character.Name + "'s Capsule", Types[TypeID.Capsule], character.ID, Location.ID, Flags.Hangar, 1, true
        );
        DogmaItems.MoveItem(character, capsule.ID, Flags.Pilot);
        SessionManager.PerformSessionUpdate(Session.CHAR_ID, callerCharacterID, new Session { ShipID = capsule.ID });

        return capsule.ID;
    }

    public PyDataType Board(ServiceCall call, PyInteger itemID)
    {
        int callerCharacterID = call.Session.CharacterID;

        if (this.Items.TryGetItem(itemID, out Ship newShip) == false)
            throw new CustomError("Ships not loaded for player and hangar!");

        Character character = this.Items.GetItem<Character>(callerCharacterID);
        Ship currentShip = this.Items.GetItem<Ship>((int)call.Session.ShipID);

        if (newShip.Singleton == false)
            throw new CustomError("TooFewSubSystemsToUndock");

        newShip.EnsureOwnership(callerCharacterID, call.Session.CorporationID, call.Session.CorporationRole, true);
        newShip.CheckPrerequisites(character);

        DogmaItems.MoveItem(character, newShip.ID, Flags.Pilot);
        SessionManager.PerformSessionUpdate(Session.CHAR_ID, callerCharacterID, new Session { ShipID = newShip.ID });

        if (currentShip.Type.ID == (int)TypeID.Capsule)
            DogmaItems.DestroyItem(currentShip);

        return null;
    }

    [MustBeInStation]
    public PyDataType AssembleShip(ServiceCall call, PyInteger itemID)
    {
        int callerCharacterID = call.Session.CharacterID;
        int stationID = call.Session.StationID;

        if (this.Items.TryGetItem(itemID, out Ship ship) == false)
            throw new CustomError("Ships not loaded for player and hangar!");

        if (ship.OwnerID != callerCharacterID)
            throw new AssembleOwnShipsOnly(ship.OwnerID);

        if (ship.Singleton)
            return new ShipAlreadyAssembled(ship.Type);

        ItemEntity split = DogmaItems.SplitStack(ship, 1);
        DogmaItems.SetSingleton(split, true);

        return null;
    }

    public PyDataType AssembleShip(ServiceCall call, PyList itemIDs)
    {
        foreach (PyInteger itemID in itemIDs.GetEnumerable<PyInteger>())
            this.AssembleShip(call, itemID);

        return null;
    }

    protected override long MachoResolveObject(ServiceCall call, ServiceBindParams parameters)
    {
        return parameters.ExtraValue switch
        {
            (int)GroupID.SolarSystem => Database.CluResolveAddress("solarsystem", parameters.ObjectID),
            (int)GroupID.Station => Database.CluResolveAddress("station", parameters.ObjectID),
            _ => throw new CustomError("Unknown item's groupID")
        };
    }

    protected override BoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams)
    {
        if (this.MachoResolveObject(call, bindParams) != BoundServiceManager.MachoNet.NodeID)
            throw new CustomError("Trying to bind an object that does not belong to us!");

        if (bindParams.ExtraValue != (int)GroupID.Station && bindParams.ExtraValue != (int)GroupID.SolarSystem)
            throw new CustomError("Cannot bind ship service to non-solarsystem and non-station locations");

        if (this.Items.TryGetItem(bindParams.ObjectID, out ItemEntity location) == false)
            throw new CustomError("This bind request does not belong here");

        if (location.Type.Group.ID != bindParams.ExtraValue)
            throw new CustomError("Location and group do not match");

        return new ship(
            location,
            this.Items,
            BoundServiceManager,
            SessionManager,
            this.DogmaNotifications,
            call.Session,
            this.SolarSystems,
            DogmaItems,
            NotificationSender
        );
    }

    /// <summary>
    /// Undock from station.
    /// 
    /// IMPORTANT: This method ONLY performs the session change.
    /// The DoDestinyUpdate notification is sent by beyonce::GetFormations()
    /// to ensure proper timing (client must have ballpark ready first).
    /// </summary>
    [MustBeInStation]
    public PyDataType Undock(ServiceCall call, PyBool animate)
    {
        var session = call.Session;
        if (session == null)
            throw new Exception("Undock: No session attached.");

        int charID    = session.CharacterID;
        int shipID    = session.ShipID ?? 0;
        int stationID = session.StationID;

        Console.WriteLine($"[ship] Undock() START: char={charID}, station={stationID}, shipID={shipID}");

        // ----------------------------
        // 1. STATIC STATION LOOKUP
        // ----------------------------
        var station = this.Items.GetStaticStation(stationID);
        if (station == null)
            throw new Exception($"Static station {stationID} missing.");

        int solarSystemID   = station.SolarSystemID;
        int constellationID = station.ConstellationID;
        int regionID        = station.RegionID;

        Console.WriteLine($"[ship] Undock: system={solarSystemID}, constellation={constellationID}, region={regionID}");

        // ----------------------------
        // 2. UPDATE SHIP POSITION
        // ----------------------------
        // Set undock position on the ship entity so beyonce can use it
        if (Items.TryGetItem(shipID, out ItemEntity shipEntity))
        {
            double undockX = (double)station.X + 1000.0;
            double undockY = (double)station.Y + 500.0;
            double undockZ = (double)station.Z + 1000.0;


            // Update ship's coordinates for the undock position
            shipEntity.X = undockX;
            shipEntity.Y = undockY;
            shipEntity.Z = undockZ;

            Console.WriteLine($"[ship] Undock: Set ship position to ({undockX:F0}, {undockY:F0}, {undockZ:F0})");
        }

        // ----------------------------
        // 3. SESSION CHANGE
        // ----------------------------
        var delta = new Session();

        delta[Session.STATION_ID]       = new PyNone();
        delta[Session.LOCATION_ID]      = (PyInteger)solarSystemID;
        delta[Session.SOLAR_SYSTEM_ID]  = (PyInteger)solarSystemID;
        delta[Session.SOLAR_SYSTEM_ID2] = (PyInteger)solarSystemID;
        delta[Session.CONSTELLATION_ID] = (PyInteger)constellationID;
        delta[Session.REGION_ID]        = (PyInteger)regionID;
        delta[Session.SHIP_ID]          = (PyInteger)shipID;

        Console.WriteLine("[ship] Undock: Performing session update...");
        this.SessionManager.PerformSessionUpdate(Session.CHAR_ID, charID, delta);
        Console.WriteLine("[ship] Undock: Session update completed");

        // ----------------------------
        // 4. RETURN - NO DoDestinyUpdate HERE!
        // ----------------------------
        // The client will:
        //   1. Receive session change notification
        //   2. Call AddBallpark() which creates the destiny.Ballpark
        //   3. Bind to beyonce service
        //   4. Call beyonce::GetFormations()
        //   5. beyonce::GetFormations() sends DoDestinyUpdate notification
        // 
        // This ordering ensures the client's ballpark is ready to receive the state.

        Console.WriteLine("[ship] Undock() COMPLETE - returning PyNone (beyonce will send state)");
        return new PyNone();
    }
}