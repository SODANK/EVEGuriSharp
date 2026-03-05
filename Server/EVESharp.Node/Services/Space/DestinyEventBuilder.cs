using System;
using System.Collections.Generic;
using EVESharp.Destiny;
using EVESharp.Types;
using EVESharp.Types.Collections;

namespace EVESharp.Node.Services.Space
{
    /// <summary>
    /// Static builder for DoDestiny_* event tuples.
    /// Each method returns a PyList of (stamp, (methodName, (args...))) tuples
    /// that can be wrapped and sent as DoDestinyUpdate notifications.
    /// </summary>
    public static class DestinyEventBuilder
    {
        private static int GetStamp()
        {
            long eveEpoch = new DateTime(2003, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
            return (int)((DateTime.UtcNow.Ticks - eveEpoch) / 10000000 % int.MaxValue);
        }

        public static PyList BuildStop(int ballID)
        {
            return BuildEvent("SetBallMass", new PyTuple(2)
            {
                [0] = new PyInteger(ballID),
                [1] = new PyInteger(0) // placeholder - Stop is handled via binary state
            });
        }

        public static PyList BuildGotoPoint(int ballID, double x, double y, double z)
        {
            return BuildEvent("GotoPoint", new PyTuple(4)
            {
                [0] = new PyInteger(ballID),
                [1] = new PyDecimal(x),
                [2] = new PyDecimal(y),
                [3] = new PyDecimal(z)
            });
        }

        public static PyList BuildFollowBall(int ballID, int targetBallID, float range)
        {
            return BuildEvent("FollowBall", new PyTuple(3)
            {
                [0] = new PyInteger(ballID),
                [1] = new PyInteger(targetBallID),
                [2] = new PyDecimal(range)
            });
        }

        public static PyList BuildOrbit(int ballID, int targetBallID, float range)
        {
            return BuildEvent("Orbit", new PyTuple(3)
            {
                [0] = new PyInteger(ballID),
                [1] = new PyInteger(targetBallID),
                [2] = new PyDecimal(range)
            });
        }

        public static PyList BuildWarpTo(int ballID, double x, double y, double z, double speed, int effectStamp)
        {
            return BuildEvent("WarpTo", new PyTuple(6)
            {
                [0] = new PyInteger(ballID),
                [1] = new PyDecimal(x),
                [2] = new PyDecimal(y),
                [3] = new PyDecimal(z),
                [4] = new PyDecimal(speed),
                [5] = new PyInteger(effectStamp)
            });
        }

        public static PyList BuildSetSpeedFraction(int ballID, double fraction)
        {
            return BuildEvent("SetSpeedFraction", new PyTuple(2)
            {
                [0] = new PyInteger(ballID),
                [1] = new PyDecimal(fraction)
            });
        }

        public static PyList BuildSetBallVelocity(int ballID, double vx, double vy, double vz)
        {
            return BuildEvent("SetBallVelocity", new PyTuple(4)
            {
                [0] = new PyInteger(ballID),
                [1] = new PyDecimal(vx),
                [2] = new PyDecimal(vy),
                [3] = new PyDecimal(vz)
            });
        }

        /// <summary>
        /// Build an AddBalls event containing destiny binary + slims for new arrivals.
        /// </summary>
        public static PyList BuildAddBalls(IEnumerable<BubbleEntity> entities, int solarSystemID, int stamp)
        {
            var balls = new List<Ball>();
            var slims = new PyList();

            foreach (var ent in entities)
            {
                balls.Add(ent.ToBall());
                slims.Add(BuildSlimFromEntity(ent, solarSystemID));
            }

            byte[] destinyBinary = DestinyBinaryEncoder.BuildFullState(balls, stamp, 1);

            var args = new PyTuple(3)
            {
                [0] = new PyBuffer(destinyBinary),
                [1] = slims,
                [2] = new PyDictionary()  // damageDict - client does (state, slims, damageDict) = chunk
            };

            return BuildEvent("AddBalls", args);
        }

        /// <summary>
        /// Build a RemoveBalls event for entities leaving the bubble.
        /// </summary>
        public static PyList BuildRemoveBalls(IEnumerable<int> ballIDs)
        {
            var idList = new PyList();
            foreach (int id in ballIDs)
                idList.Add(new PyInteger(id));

            return BuildEvent("RemoveBalls", new PyTuple(1) { [0] = idList });
        }

        /// <summary>
        /// Wrap a list of events into the full DoDestinyUpdate notification format:
        /// PyTuple(3) { events, waitForBubble, dogmaMessages }
        /// </summary>
        public static PyTuple WrapAsNotification(PyList events)
        {
            return new PyTuple(3)
            {
                [0] = events,
                [1] = new PyBool(false),
                [2] = new PyList()
            };
        }

        // =====================================================================
        //  HELPERS
        // =====================================================================

        private static PyList BuildEvent(string methodName, PyTuple args)
        {
            int stamp = GetStamp();

            var innerCall  = new PyTuple(2) { [0] = new PyString(methodName), [1] = args };
            var eventTuple = new PyTuple(2) { [0] = new PyInteger(stamp),     [1] = innerCall };

            var events = new PyList();
            events.Add(eventTuple);
            return events;
        }

        private static PyObjectData BuildSlimFromEntity(BubbleEntity ent, int solarSystemID)
        {
            var d = new PyDictionary
            {
                ["itemID"]          = new PyInteger(ent.ItemID),
                ["typeID"]          = new PyInteger(ent.TypeID),
                ["groupID"]         = new PyInteger(ent.GroupID),
                ["ownerID"]         = new PyInteger(ent.OwnerID),
                ["locationID"]      = new PyInteger(solarSystemID),
                ["categoryID"]      = new PyInteger(ent.CategoryID),
                ["name"]            = new PyString(ent.Name ?? "Unknown"),
                ["corpID"]          = new PyInteger(ent.CorporationID),
                ["allianceID"]      = new PyInteger(ent.AllianceID),
                ["charID"]          = new PyInteger(ent.CharacterID),
                ["dunObjectID"]     = new PyNone(),
                ["jumps"]           = new PyList(),
                ["securityStatus"]  = new PyDecimal(0.0),
                ["orbitalVelocity"] = new PyDecimal(0.0),
                ["warFactionID"]    = new PyNone()
            };

            return new PyObjectData("util.KeyVal", d);
        }
    }
}
