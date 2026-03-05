using System;
using EVESharp.Destiny;
using EVESharp.EVE.Notifications;
using EVESharp.Types;
using EVESharp.Types.Collections;
using Serilog;

namespace EVESharp.Node.Services.Space
{
    /// <summary>
    /// Sends DoDestinyUpdate notifications to characters in a solar system.
    /// Uses "solarsystemid2" broadcast type to match EVE client routing.
    /// </summary>
    public class DestinyBroadcaster
    {
        private readonly INotificationSender mNotificationSender;
        private readonly ILogger mLog;

        public DestinyBroadcaster(INotificationSender notificationSender, ILogger logger)
        {
            mNotificationSender = notificationSender;
            mLog = logger;
        }

        /// <summary>
        /// Send destiny events to all characters in a solar system (bubble broadcast).
        /// </summary>
        public void BroadcastToSystem(int solarSystemID, PyList events)
        {
            if (events == null) return;

            PyTuple notification = DestinyEventBuilder.WrapAsNotification(events);
            SendToSystem(solarSystemID, notification);
        }

        /// <summary>
        /// Send destiny events to a specific character via solar system broadcast.
        /// </summary>
        public void SendToCharacterInSystem(int solarSystemID, PyList events)
        {
            if (events == null) return;

            PyTuple notification = DestinyEventBuilder.WrapAsNotification(events);
            SendToSystem(solarSystemID, notification);
        }

        private void SendToSystem(int solarSystemID, PyTuple notification)
        {
            try
            {
                // CRITICAL: idType MUST be "solarsystemid2" for EVE client routing
                mNotificationSender.SendNotification(
                    "DoDestinyUpdate",
                    "solarsystemid2",
                    solarSystemID,
                    notification
                );
            }
            catch (Exception ex)
            {
                mLog.Error(ex, "[DestinyBroadcaster] Error sending to system {SolarSystemID}: {Message}", solarSystemID, ex.Message);
            }
        }
    }
}
