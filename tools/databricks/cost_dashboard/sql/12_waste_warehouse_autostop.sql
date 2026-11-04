-- =============================================================================
-- Dataset 12  waste_warehouse_autostop  Page 4 Waste              Visual: table (+ counter)
-- Waste item 3: SQL warehouses (not deleted) with auto-stop off or above the
-- Learn default "recommended for typical use":
--   serverless   > 10 min   (UI minimum 5)
--   pro, classic > 45 min   (minimum 10)
-- The system table column is auto_stop_minutes; the Warehouses API field is
-- auto_stop_mins.
--
-- Estimated waste = the larger of
--   (a) tail estimate: stop events x (setting - target) / 60 x cost per uptime hour
--       (uptime = STARTING to the next STOPPED, the Learn "most uptime" pattern)
--   (b) idle billed hours: hours with no statement on the warehouse (query.history)
-- The larger one is used, not the sum: both measure the same idle time.
-- Auto-stop off (0) has no stop events, so (b) applies.
-- Stop events include manual stops, so (a) is an upper bound.
-- Monthly saving = period waste x 30 / days in period.
--
-- Tables  system.compute.warehouses, system.compute.warehouse_events,
--         system.query.history (regional), system.billing.usage,
--         system.billing.list_prices, system.access.workspaces_latest (global).
-- Params  :start_date, :end_date (DATE).
-- =============================================================================
WITH priced AS (
  -- One row per usage record with its USD list cost (Learn: "Join the pricing with usage tables").
  SELECT
    u.*,
    CAST(u.usage_quantity AS DOUBLE)
      * CAST(COALESCE(p.pricing.effective_list.default, p.pricing.default) AS DOUBLE) AS list_cost_usd
  FROM system.billing.usage AS u
  LEFT JOIN system.billing.list_prices AS p
    ON  p.sku_name = u.sku_name
    AND p.cloud = u.cloud
    AND p.usage_unit = u.usage_unit
    AND p.currency_code = 'USD'
    AND u.usage_end_time >= p.price_start_time
    AND (p.price_end_time IS NULL OR u.usage_end_time < p.price_end_time)
  WHERE u.usage_date BETWEEN :start_date AND :end_date
),
warehouses_latest AS (
  SELECT *
  FROM (SELECT wh.*, ROW_NUMBER() OVER (PARTITION BY wh.workspace_id, wh.warehouse_id ORDER BY wh.change_time DESC) AS rn
        FROM system.compute.warehouses AS wh) AS t
  WHERE rn = 1
),
flagged AS (
  SELECT
    workspace_id, warehouse_id, warehouse_name, warehouse_type, warehouse_size,
    min_clusters, max_clusters, auto_stop_minutes,
    CASE WHEN warehouse_type = 'SERVERLESS' THEN 10 ELSE 45 END AS target_auto_stop_min   -- EDIT HERE
  FROM warehouses_latest
  WHERE delete_time IS NULL
    AND (auto_stop_minutes = 0
         OR auto_stop_minutes > CASE WHEN warehouse_type = 'SERVERLESS' THEN 10 ELSE 45 END)
),
warehouse_hour_cost AS (
  SELECT workspace_id, usage_metadata.warehouse_id AS warehouse_id,
         date_trunc('HOUR', usage_start_time) AS usage_hour,
         SUM(list_cost_usd) AS cost_usd,
         max_by(identity_metadata.owned_by, usage_end_time) AS owned_by
  FROM priced
  WHERE usage_metadata.warehouse_id IS NOT NULL
  GROUP BY workspace_id, usage_metadata.warehouse_id, date_trunc('HOUR', usage_start_time)
),
warehouse_busy_hours AS (
  SELECT DISTINCT q.workspace_id, q.compute.warehouse_id AS warehouse_id, busy_hour AS usage_hour
  FROM system.query.history AS q
  LATERAL VIEW explode(sequence(date_trunc('HOUR', q.start_time),
                                date_trunc('HOUR', COALESCE(q.end_time, q.update_time, q.start_time)),
                                INTERVAL 1 HOUR)) hrs AS busy_hour
  WHERE q.compute.type = 'WAREHOUSE'
    AND q.start_time >= date_sub(:start_date, 1) AND q.start_time < date_add(:end_date, 1)
),
cost_and_idle AS (
  SELECT
    h.workspace_id, h.warehouse_id,
    max_by(h.owned_by, h.usage_hour)                                    AS owned_by,
    SUM(h.cost_usd)                                                     AS cost_usd,
    COUNT(*)                                                            AS billed_hours,
    SUM(CASE WHEN b.usage_hour IS NULL THEN 1 ELSE 0 END)               AS idle_hours,
    SUM(CASE WHEN b.usage_hour IS NULL THEN h.cost_usd ELSE 0 END)      AS idle_cost_usd
  FROM warehouse_hour_cost AS h
  LEFT JOIN warehouse_busy_hours AS b
    ON b.workspace_id = h.workspace_id AND b.warehouse_id = h.warehouse_id AND b.usage_hour = h.usage_hour
  GROUP BY h.workspace_id, h.warehouse_id
),
events AS (
  SELECT
    workspace_id, warehouse_id, event_type, event_time,
    MIN(CASE WHEN event_type = 'STOPPED' THEN event_time END) OVER (
      PARTITION BY workspace_id, warehouse_id ORDER BY event_time
      ROWS BETWEEN 1 FOLLOWING AND UNBOUNDED FOLLOWING)                 AS next_stopped_time
  FROM system.compute.warehouse_events
  WHERE event_time >= date_sub(:start_date, 7) AND event_time < date_add(:end_date, 1)
),
stops AS (
  SELECT workspace_id, warehouse_id, COUNT(*) AS stop_events
  FROM system.compute.warehouse_events
  WHERE event_type = 'STOPPED'
    AND event_time >= :start_date AND event_time < date_add(:end_date, 1)
  GROUP BY workspace_id, warehouse_id
),
uptime AS (
  -- STARTING -> next STOPPED (still running: now), clipped to the selected period.
  SELECT workspace_id, warehouse_id,
         SUM(GREATEST(0, timestampdiff(SECOND,
               GREATEST(event_time, CAST(:start_date AS TIMESTAMP)),
               LEAST(COALESCE(next_stopped_time, current_timestamp()), CAST(date_add(:end_date, 1) AS TIMESTAMP))
             ))) / 3600.0 AS uptime_hours
  FROM events
  WHERE event_type = 'STARTING'
  GROUP BY workspace_id, warehouse_id
),
scored AS (
  SELECT
    f.*,
    ci.owned_by,
    COALESCE(ci.cost_usd, 0)                                             AS cost_usd,
    COALESCE(ci.billed_hours, 0)                                         AS billed_hours,
    COALESCE(ci.idle_hours, 0)                                           AS idle_hours,
    COALESCE(ci.idle_cost_usd, 0)                                        AS idle_cost_usd,
    COALESCE(st.stop_events, 0)                                          AS stop_events,
    up.uptime_hours,
    CASE WHEN f.auto_stop_minutes > f.target_auto_stop_min AND up.uptime_hours > 0
         THEN COALESCE(st.stop_events, 0) * (f.auto_stop_minutes - f.target_auto_stop_min) / 60.0
              * COALESCE(ci.cost_usd, 0) / up.uptime_hours
         ELSE 0 END                                                      AS tail_estimate_usd
  FROM flagged AS f
  LEFT JOIN cost_and_idle AS ci      ON ci.workspace_id = f.workspace_id AND ci.warehouse_id = f.warehouse_id
  LEFT JOIN stops AS st              ON st.workspace_id = f.workspace_id AND st.warehouse_id = f.warehouse_id
  LEFT JOIN uptime AS up             ON up.workspace_id = f.workspace_id AND up.warehouse_id = f.warehouse_id
)
SELECT
  '3 SQL warehouse auto-stop too high'                                   AS waste_item,
  s.workspace_id,
  COALESCE(w.workspace_name, concat('workspace ', s.workspace_id))       AS workspace_name,
  'SQL warehouse'                                                        AS resource_type,
  s.warehouse_id                                                         AS resource_id,
  s.warehouse_name                                                       AS resource_name,
  s.owned_by                                                             AS owner,
  concat(s.warehouse_type, ' ', COALESCE(s.warehouse_size, ''), ', auto-stop ',
         CASE WHEN s.auto_stop_minutes = 0 THEN 'off' ELSE concat(CAST(s.auto_stop_minutes AS STRING), ' min') END,
         '; ', CAST(s.stop_events AS STRING), ' stops, ', CAST(s.idle_hours AS STRING), ' idle billed hours')
                                                                         AS finding,
  CASE WHEN s.warehouse_type = 'SERVERLESS'
       THEN 'Set auto-stop to 10 min (UI minimum 5): SQL Warehouses > Edit > Auto stop, or auto_stop_mins in the Warehouses API'
       ELSE 'Upgrade to serverless (kebab menu > Upgrade to Serverless) with auto-stop 10 min; if it must stay pro/classic, set auto-stop to 45 min or lower (minimum 10)'
  END                                                                    AS recommended_fix,
  ROUND(s.cost_usd, 2)                                                   AS affected_cost_usd,
  ROUND(GREATEST(s.tail_estimate_usd, s.idle_cost_usd), 2)               AS estimated_waste_usd,
  ROUND(GREATEST(s.tail_estimate_usd, s.idle_cost_usd) * 30.0 / (datediff(:end_date, :start_date) + 1), 2)
                                                                         AS estimated_monthly_saving_usd,
  'Larger of: stops x (setting - target) x cost per uptime hour; cost of idle billed hours'
                                                                         AS saving_basis,
  s.warehouse_type,
  s.auto_stop_minutes,
  s.target_auto_stop_min,
  s.stop_events,
  ROUND(s.uptime_hours, 1)                                               AS uptime_hours,
  s.idle_hours
FROM scored AS s
LEFT JOIN system.access.workspaces_latest AS w ON w.workspace_id = s.workspace_id
ORDER BY estimated_monthly_saving_usd DESC, affected_cost_usd DESC;
