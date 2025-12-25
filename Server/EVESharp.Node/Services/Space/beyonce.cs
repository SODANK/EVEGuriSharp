using System;
using System.Collections.Generic;
using EVESharp.Database.Types;
using EVESharp.Destiny;
using EVESharp.EVE.Data.Inventory;
using EVESharp.EVE.Data.Inventory.Items;
using EVESharp.EVE.Network.Services;
using EVESharp.EVE.Notifications;
using EVESharp.EVE.Sessions;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.Node.Services.Space
{
    /// <summary>
    /// The 'beyonce' service is what the client expects for ballpark operations.
    /// 
    /// CRITICAL TIMING: DoDestinyUpdate must be sent AFTER GetFormations() is called,
    /// NOT during Undock. The client creates its ballpark, binds to beyonce, calls
    /// GetFormations(), THEN is ready to receive DoDestinyUpdate.
    /// </summary>
    [ConcreteService("beyonce")]
    public class beyonce : ClientBoundService
    {
        public override AccessLevel AccessLevel => AccessLevel.None;

        private IItems              Items              { get; }
        private INotificationSender NotificationSender { get; }

        private Ballpark mBallpark;
        private int      mSolarSystemID;
        private bool     mInitialStateSent = false;  // Track if we've sent initial state

        // =====================================================================
        //  CONSTRUCTORS
        // =====================================================================

        /// <summary>
        /// Global / unbound constructor.
        /// </summary>
        public beyonce(IBoundServiceManager manager, IItems items, INotificationSender notificationSender)
            : base(manager)
        {
            Console.WriteLine("[beyonce] Global service constructed");
            this.Items              = items;
            this.NotificationSender = notificationSender;
        }

        /// <summary>
        /// Bound constructor - called for each solar system binding.
        /// </summary>
        internal beyonce(
            IBoundServiceManager manager,
            Session              session,
            int                  objectID,
            IItems               items,
            INotificationSender  notificationSender)
            : base(manager, session, objectID)
        {
            this.Items              = items;
            this.NotificationSender = notificationSender;
            this.mSolarSystemID     = objectID;

            int ownerID   = session.CharacterID;
            int shipID    = session.ShipID   ?? 0;
            int stationID = session.StationID;

            Console.WriteLine($"[beyonce] Bound ctor: solarSystemID={mSolarSystemID}, charID={ownerID}, shipID={shipID}");

            // Create ballpark for this solar system
            mBallpark = new Ballpark(mSolarSystemID, ownerID);

            // Auto-load the player's ship
            if (shipID != 0 && Items.TryGetItem(shipID, out ItemEntity shipEntity))
            {
                Console.WriteLine($"[beyonce] Auto-adding ship entity {shipID}");
                mBallpark.AddEntity(shipEntity);
            }

            // Auto-load the station if we have it (for undock scenarios)
            if (stationID != 0 && Items.TryGetItem(stationID, out ItemEntity stationEntity))
            {
                Console.WriteLine($"[beyonce] Auto-adding station entity {stationID}");
                mBallpark.AddEntity(stationEntity);
            }

            Console.WriteLine("[beyonce] Ballpark created - waiting for GetFormations to send state");
        }

        // =====================================================================
        //  MACHO BINDING
        // =====================================================================

        protected override long MachoResolveObject(ServiceCall call, ServiceBindParams bindParams)
        {
            Console.WriteLine($"[beyonce] MachoResolveObject: objectID={bindParams.ObjectID}");
            return BoundServiceManager.MachoNet.NodeID;
        }

        protected override BoundService CreateBoundInstance(ServiceCall call, ServiceBindParams bindParams)
        {
            Console.WriteLine($"[beyonce] CreateBoundInstance: objectID={bindParams.ObjectID}, charID={call.Session.CharacterID}");
            return new beyonce(BoundServiceManager, call.Session, bindParams.ObjectID, this.Items, this.NotificationSender);
        }

        // =====================================================================
        //  PUBLIC API - Called by the client
        // =====================================================================

        /// <summary>
        /// GetFormations - Returns ship formation data.
        /// 
        /// CRITICAL: This is called by the client AFTER it has created its ballpark
        /// and is ready to receive DoDestinyUpdate. We send the initial state here.
        /// </summary>
        public PyDataType GetFormations(ServiceCall call)
        {
            Console.WriteLine("[beyonce] GetFormations() called");

            // Send initial state if not already sent
            if (!mInitialStateSent && mBallpark != null)
            {
                mInitialStateSent = true;
                SendInitialDestinyState(call.Session);
            }

            // Return empty formations tuple
            Console.WriteLine("[beyonce] GetFormations() returning empty tuple");
            return new PyTuple(0);
        }

        /// <summary>
        /// Sends the initial DoDestinyUpdate notification to the client.
        /// Called from GetFormations to ensure proper timing.
        /// </summary>
        private void SendInitialDestinyState(Session session)
        {
            int charID = session.CharacterID;
            int shipID = session.ShipID ?? 0;

            Console.WriteLine($"[beyonce] SendInitialDestinyState: charID={charID}, shipID={shipID}, system={mSolarSystemID}");

            try
            {
                // Build the destiny state
                PyDataType destinyState = BuildSnapshot(mSolarSystemID, shipID, session);

                // Wrap in DoDestinyUpdate format: (state_list, waitForBubble, dogmaMessages)
                PyTuple notificationData = new PyTuple(3)
                {
                    [0] = destinyState,
                    [1] = new PyBool(false),  // waitForBubble
                    [2] = new PyList()        // dogmaMessages
                };

                Console.WriteLine($"[beyonce] Sending DoDestinyUpdate notification to char {charID}");
                NotificationSender.SendNotification("DoDestinyUpdate", NotificationIdType.Character, charID, notificationData);
                Console.WriteLine("[beyonce] DoDestinyUpdate notification sent successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[beyonce] ERROR sending DoDestinyUpdate: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        /// <summary>
        /// Stop - Called by the client when the ballpark is being torn down.
        /// </summary>
        public PyDataType Stop(ServiceCall call)
        {
            Console.WriteLine($"[beyonce] Stop() called");
            mBallpark = null;
            mInitialStateSent = false;
            return new PyNone();
        }

        /// <summary>
        /// UpdateStateRequest - Alternative way for client to request state.
        /// Some client versions may call this instead of relying on notification.
        /// </summary>
        public PyDataType UpdateStateRequest(ServiceCall call)
        {
            EnsureBallpark(call.Session);

            int shipID = call.Session.ShipID ?? 0;
            
            Console.WriteLine($"[beyonce] UpdateStateRequest(): charID={call.Session.CharacterID}, shipID={shipID}");

            return BuildSnapshot(mSolarSystemID, shipID, call.Session);
        }

        /// <summary>
        /// GetInitialState - Alternative method name some client versions use.
        /// </summary>
        public PyDataType GetInitialState(ServiceCall call)
        {
            Console.WriteLine("[beyonce] GetInitialState() - delegating to UpdateStateRequest");
            return UpdateStateRequest(call);
        }

        // =====================================================================
        //  INTERNAL HELPERS
        // =====================================================================

        private void EnsureBallpark(Session session)
        {
            if (mBallpark != null)
                return;

            int solarSystemID = session.SolarSystemID ?? 0;
            int ownerID       = session.CharacterID;
            int shipID        = session.ShipID ?? 0;
            int stationID     = session.StationID;

            Console.WriteLine("[beyonce] EnsureBallpark: creating ballpark");

            mBallpark = new Ballpark(solarSystemID, ownerID);

            if (shipID != 0 && Items.TryGetItem(shipID, out ItemEntity shipEntity))
                mBallpark.AddEntity(shipEntity);

            if (stationID != 0 && Items.TryGetItem(stationID, out ItemEntity stationEntity))
                mBallpark.AddEntity(stationEntity);
        }

        /// <summary>
        /// Build the Apocrypha-style ballpark snapshot.
        /// Format: [(timestamp, ('SetState', (bagKeyVal,)))]
        /// </summary>
        private PyDataType BuildSnapshot(int solarSystemID, int shipID, Session sess)
        {
            Console.WriteLine($"[beyonce] Building snapshot system={solarSystemID}, ship={shipID}");

            // Destiny state buffer
            byte[] destinyData = BuildDestinyState(solarSystemID, shipID, sess);

            if (destinyData == null || destinyData.Length == 0)
                Console.WriteLine($"[beyonce] WARNING: Destiny state is empty");
            else
                Console.WriteLine($"[beyonce] Destiny state buffer: {destinyData.Length} bytes");

            // Root bag
            var bagDict = new PyDictionary();

            bagDict["aggressors"] = new PyDictionary();
            bagDict["droneState"] = BuildEmptyDroneState();
            bagDict["solItem"]    = BuildSolItem(solarSystemID);
            bagDict["state"]      = new PyBuffer(destinyData ?? Array.Empty<byte>());
            bagDict["ego"]        = new PyInteger(shipID);

            // slims - ONLY actual space entities (not solar system)
            var slims = new PyList();

            // Ship slim
            if (mBallpark != null && mBallpark.TryGetEntity(shipID, out var playerShipEntity))
            {
                string shipName = playerShipEntity.Name ?? playerShipEntity.Type?.Name ?? "Ship";

                var slimShipDict = new PyDictionary
                {
                    ["itemID"]     = new PyInteger(shipID),
                    ["typeID"]     = new PyInteger(playerShipEntity.Type.ID),
                    ["groupID"]    = new PyInteger(playerShipEntity.Type.Group.ID),
                    ["ownerID"]    = new PyInteger(sess.CharacterID),
                    ["locationID"] = new PyInteger(solarSystemID),
                    ["categoryID"] = new PyInteger(playerShipEntity.Type.Group.Category.ID),
                    ["name"]       = new PyString(shipName),
                    ["corpID"]     = new PyInteger(sess.CorporationID),
                    ["allianceID"] = new PyInteger(0),
                    ["charID"]     = new PyInteger(sess.CharacterID)
                };

                slims.Add(new PyObjectData("util.KeyVal", slimShipDict));
                Console.WriteLine("[beyonce] Added SHIP slim");
            }
            else
            {
                Console.WriteLine("[beyonce] WARNING: Ship entity not found for slim");
            }

            // Station slim (if in ballpark)
            int stationID = sess.StationID;
            if (stationID != 0 && mBallpark != null && mBallpark.TryGetEntity(stationID, out var stationEnt))
            {
                var slimStationDict = new PyDictionary
                {
                    ["itemID"]     = new PyInteger(stationEnt.ID),
                    ["typeID"]     = new PyInteger(stationEnt.Type.ID),
                    ["groupID"]    = new PyInteger(stationEnt.Type.Group.ID),
                    ["ownerID"]    = new PyInteger(stationEnt.OwnerID),
                    ["locationID"] = new PyInteger(solarSystemID),
                    ["categoryID"] = new PyInteger(stationEnt.Type.Group.Category.ID),
                    ["name"]       = new PyString(stationEnt.Type.Name),
                    ["corpID"]     = new PyInteger(0),
                    ["allianceID"] = new PyInteger(0),
                    ["charID"]     = new PyInteger(0)
                };

                slims.Add(new PyObjectData("util.KeyVal", slimStationDict));
                Console.WriteLine("[beyonce] Added STATION slim");
            }

            bagDict["slims"] = slims;

            // Additional fields
            bagDict["damageState"]     = new PyDictionary();
            bagDict["effectStates"]    = new PyList();
            bagDict["allianceBridges"] = new PyList();

            Console.WriteLine("[beyonce] Snapshot bag constructed");

            // Package into destiny event list: [(timestamp, ('SetState', (bagKeyVal,)))]
            var bagKeyVal = new PyObjectData("util.KeyVal", bagDict);

            var stateCallArgs = new PyTuple(1) { [0] = bagKeyVal };

            var innerCall = new PyTuple(2)
            {
                [0] = new PyString("SetState"),
                [1] = stateCallArgs
            };

            var eventTuple = new PyTuple(2)
            {
                [0] = new PyInteger(Environment.TickCount),
                [1] = innerCall
            };

            var events = new PyList();
            events.Add(eventTuple);

            return events;
        }

        /// <summary>
        /// Build the Destiny binary state buffer.
        /// </summary>
        private byte[] BuildDestinyState(int solarSystemID, int egoShipID, Session sess)
        {
            if (mBallpark == null || mBallpark.Entities.Count == 0)
            {
                Console.WriteLine("[beyonce] WARNING: No entities in ballpark");
                return Array.Empty<byte>();
            }

            var balls = new List<Ball>();

            foreach (var kvp in mBallpark.Entities)
            {
                ItemEntity ent = kvp.Value;
                bool isEgo = (ent.ID == egoShipID);

                double x = ent.X ?? 0;
                double y = ent.Y ?? 0;
                double z = ent.Z ?? 0;

                Console.WriteLine($"[beyonce] Building ball for entity {ent.ID}, isEgo={isEgo}, pos=({x:F0},{y:F0},{z:F0})");

                // Determine flags based on entity type
                BallFlag flags = BallFlag.IsMassive;
                BallMode mode = BallMode.Rigid;

                if (isEgo)
                {
                    // Player ship: movable, interactive
                    flags = BallFlag.IsFree | BallFlag.IsMassive | BallFlag.IsInteractive;
                    mode = BallMode.Stop;
                }
                else if (ent.Type?.Group?.Category?.ID == 3) // Station category
                {
                    // Station: rigid, global
                    flags = BallFlag.IsGlobal | BallFlag.IsMassive;
                    mode = BallMode.Rigid;
                }

                var header = new BallHeader
                {
                    ItemId   = ent.ID,
                    Location = new Vector3 { X = x, Y = y, Z = z },
                    Radius   = isEgo ? 50.0f : 5000.0f,
                    Mode     = mode,
                    Flags    = flags
                };

                Ball ball = new Ball
                {
                    Header      = header,
                    FormationId = 0xFF
                };

                // Extra header and ball data only for non-rigid balls
                if (mode != BallMode.Rigid)
                {
                    ball.ExtraHeader = new ExtraBallHeader
                    {
                        AllianceId    = 0,
                        CorporationId = sess.CorporationID,
                        CloakMode     = CloakMode.Normal,
                        Harmonic      = -1.0f,
                        Mass          = 1000000.0
                    };

                    if (flags.HasFlag(BallFlag.IsFree))
                    {
                        ball.Data = new BallData
                        {
                            MaxVelocity   = 200f,
                            SpeedFraction = 0f,
                            Unk03         = 0f,
                            Velocity      = new Vector3 { X = 0, Y = 0, Z = 0 }
                        };
                    }
                }

                balls.Add(ball);
            }

            Console.WriteLine($"[beyonce] Built {balls.Count} ball(s)");

            // Generate timestamp
            long eveEpoch = new DateTime(2003, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
            int stamp = (int)((DateTime.UtcNow.Ticks - eveEpoch) / 10000000 % int.MaxValue);

            byte[] result = DestinyBinaryEncoder.BuildFullState(balls, stamp, 0);
            Console.WriteLine($"[beyonce] Destiny binary size: {result.Length} bytes");

            return result;
        }

        // =====================================================================
        //  HELPER BUILDERS
        // =====================================================================

        private PyObjectData BuildSolItem(int solID)
        {
            var d = new PyDictionary
            {
                ["itemID"]     = new PyInteger(solID),
                ["typeID"]     = new PyInteger(5),
                ["groupID"]    = new PyInteger(5),
                ["ownerID"]    = new PyInteger(1),
                ["locationID"] = new PyInteger(0),
                ["x"]          = new PyInteger(0),
                ["y"]          = new PyInteger(0),
                ["z"]          = new PyInteger(0),
                ["categoryID"] = new PyInteger(2),
                ["name"]       = new PyString("Solar System"),
                ["corpID"]     = new PyInteger(0),
                ["allianceID"] = new PyInteger(0),
                ["charID"]     = new PyInteger(0)
            };

            return new PyObjectData("util.KeyVal", d);
        }

        private PyObjectData BuildEmptyDroneState()
        {
            return new PyObjectData(
                "util.Rowset",
                new PyDictionary
                {
                    ["header"]   = new PyList
                    {
                        new PyString("droneID"),
                        new PyString("ownerID"),
                        new PyString("controllerID"),
                        new PyString("activityState"),
                        new PyString("typeID"),
                        new PyString("controllerOwnerID"),
                        new PyString("targetID")
                    },
                    ["RowClass"] = new PyString("util.Row"),
                    ["lines"]    = new PyList()
                }
            );
        }

        protected override void OnClientDisconnected()
        {
            Console.WriteLine("[beyonce] Client disconnected");
        }
    }
}