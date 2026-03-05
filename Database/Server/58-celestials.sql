/*
 * Populate celestial objects (Sun, Planet, Moon, AsteroidBelt, Stargate) from mapDenormalize
 * into invItems, eveNames, and invPositions so they are visible in space after undocking.
 *
 * Safe to run multiple times (uses REPLACE INTO).
 * Prerequisite: mapDenormalize must already be populated (from apo15-mysql5-v1.sql).
 */

/* Insert celestial items into invItems with locationID = solarSystemID */
REPLACE INTO invItems (itemID, typeID, ownerID, locationID, flag, contraband, singleton, quantity)
  SELECT itemID, typeID,
         IF(staStations.corporationID IS NULL, 1, staStations.corporationID),
         IF(mapDenormalize.solarSystemID IS NULL,
            IF(mapDenormalize.constellationID IS NULL,
               IF(mapDenormalize.regionID IS NULL, 9, mapDenormalize.regionID),
               mapDenormalize.constellationID),
            mapDenormalize.solarSystemID),
         0, 0, 1, 1
  FROM mapDenormalize
  LEFT JOIN staStations ON staStations.stationID = mapDenormalize.itemID
  WHERE mapDenormalize.groupID IN (6, 7, 8, 9, 10);

/* Insert missing names into eveNames */
REPLACE INTO eveNames (itemID, itemName, typeID, groupID, categoryID)
  SELECT md.itemID, md.itemName, md.typeID, md.groupID, ig.categoryID
  FROM mapDenormalize md
  LEFT JOIN invGroups ig USING (groupID)
  WHERE md.groupID IN (6, 7, 8, 9, 10)
    AND md.solarSystemID IS NOT NULL;

/* Insert positions into invPositions */
REPLACE INTO invPositions (itemID, x, y, z)
  SELECT itemID, x, y, z
  FROM mapDenormalize
  WHERE groupID IN (6, 7, 8, 9, 10)
    AND solarSystemID IS NOT NULL;
