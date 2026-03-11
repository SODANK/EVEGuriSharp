-- ============================================================================
-- Seed Venal region stations with sell orders for all marketable items at 10 ISK
-- For testing purposes only
-- ============================================================================

-- Clear any previously seeded orders (by the system character)
-- DELETE FROM mktOrders WHERE charID = 1;

INSERT INTO mktOrders
  (typeID, charID, corpID, stationID, `range`, bid, price,
   volEntered, volRemaining, issued, minVolume, accountID, duration, isCorp, escrow)
SELECT
  t.typeID,
  1 AS charID,                                                    -- system character
  1000125 AS corpID,                                              -- Guristas (Venal NPC corp)
  s.stationID,
  -1 AS `range`,                                                  -- station only
  0 AS bid,                                                       -- sell order
  10.0 AS price,                                                  -- 10 ISK
  1000000 AS volEntered,                                          -- 1 million units
  1000000 AS volRemaining,                                        -- 1 million units remaining
  (UNIX_TIMESTAMP() * 10000000 + 116444736000000000) AS issued,   -- now as Windows FILETIME
  1 AS minVolume,                                                 -- minimum 1 unit
  1000 AS accountID,                                              -- main wallet
  8760 AS duration,                                               -- 365 days in hours
  0 AS isCorp,
  0 AS escrow                                                     -- no escrow for sell orders
FROM invTypes t
CROSS JOIN staStations s
WHERE s.regionID = 10000015             -- Venal region
  AND t.marketGroupID IS NOT NULL       -- has a market group (marketable)
  AND t.published = 1                   -- is published
;
