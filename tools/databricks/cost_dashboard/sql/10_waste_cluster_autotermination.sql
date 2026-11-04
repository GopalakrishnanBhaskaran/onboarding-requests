-- =============================================================================
-- Dataset 10  waste_cluster_autotermination  Page 4 Waste   Visual: table (+ counter)
-- Waste item 1: all-purpose clusters (cluster_source UI or API) that are not
-- deleted and have auto-termination off (0 / NULL) or over 60 minutes.
--
-- Estimated waste  = list cost of the cluster's IDLE billed hours in the period.
--   Idle hour      = the busiest node of the cluster averaged under 5 % CPU
--                    (cpu_user_percent + cpu_system_percent) in that clock hour.
-- Estimated saving if auto-termination is set to 30 minutes:
--   setting > 60   : idle cost x (1 - 30 / setting)
--   setting off    : idle cost minus 30 minutes of cost per active day
--   (monthly = period saving x 30 / days in period)
-- Hours with no node_timeline rows are not judged ("Nodes that ran for less
-- than 10 minutes might not appear in the node_timeline table").
-- List price only: the Azure VM cost of the same idle hours comes on top.
--
-- Tables  system.compute.clusters, system.compute.node_timeline (regional),
--         system.billing.usage, system.billing.list_prices,
--         system.access.workspaces_latest (global).
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
clusters_latest AS (
  SELECT *
  FROM (SELECT c.*, ROW_NUMBER() OVER (PARTITION BY c.workspace_id, c.cluster_id ORDER BY c.change_time DESC) AS rn
        FROM system.compute.clusters AS c) AS t
  WHERE rn = 1
),
flagged AS (
  SELECT workspace_id, cluster_id, cluster_name, owned_by,
         COALESCE(auto_termination_minutes, 0) AS autoterm_min
  FROM clusters_latest
  WHERE cluster_source IN ('UI', 'API')
    AND delete_time IS NULL
    AND (auto_termination_minutes IS NULL OR auto_termination_minutes = 0 OR auto_termination_minutes > 60)
),
cluster_hour_cost AS (
  SELECT workspace_id, usage_metadata.cluster_id AS cluster_id,
         date_trunc('HOUR', usage_start_time) AS usage_hour,
         SUM(list_cost_usd) AS cost_usd
  FROM priced
  WHERE billing_origin_product = 'ALL_PURPOSE' AND usage_metadata.cluster_id IS NOT NULL
  GROUP BY workspace_id, usage_metadata.cluster_id, date_trunc('HOUR', usage_start_time)
),
node_hour AS (
  SELECT workspace_id, cluster_id, instance_id, date_trunc('HOUR', start_time) AS usage_hour,
         AVG(cpu_user_percent + cpu_system_percent) AS node_cpu_pct
  FROM system.compute.node_timeline
  WHERE start_time >= :start_date AND start_time < date_add(:end_date, 1)
  GROUP BY workspace_id, cluster_id, instance_id, date_trunc('HOUR', start_time)
),
cluster_hour_cpu AS (
  SELECT workspace_id, cluster_id, usage_hour, MAX(node_cpu_pct) AS max_node_cpu_pct
  FROM node_hour
  GROUP BY workspace_id, cluster_id, usage_hour
),
hours AS (
  SELECT h.workspace_id, h.cluster_id, h.usage_hour, h.cost_usd,
         c.max_node_cpu_pct < 5 AS is_idle                       -- EDIT HERE: idle CPU threshold (%)
  FROM cluster_hour_cost AS h
  LEFT JOIN cluster_hour_cpu AS c
    ON c.workspace_id = h.workspace_id AND c.cluster_id = h.cluster_id AND c.usage_hour = h.usage_hour
),
per_cluster AS (
  SELECT
    f.workspace_id, f.cluster_id, f.cluster_name, f.owned_by, f.autoterm_min,
    COALESCE(SUM(h.cost_usd), 0)                                         AS cost_usd,
    COUNT(h.usage_hour)                                                  AS billed_hours,
    SUM(CASE WHEN h.is_idle THEN 1 ELSE 0 END)                           AS idle_hours,
    COALESCE(SUM(CASE WHEN h.is_idle THEN h.cost_usd ELSE 0 END), 0)     AS idle_cost_usd,
    COUNT(DISTINCT CASE WHEN h.is_idle = FALSE THEN to_date(h.usage_hour) END) AS active_days,
    COALESCE(try_divide(SUM(h.cost_usd), COUNT(h.usage_hour)), 0)        AS avg_hour_cost_usd
  FROM flagged AS f
  LEFT JOIN hours AS h
    ON h.workspace_id = f.workspace_id AND h.cluster_id = f.cluster_id
  GROUP BY f.workspace_id, f.cluster_id, f.cluster_name, f.owned_by, f.autoterm_min
),
scored AS (
  SELECT *,
         CASE WHEN autoterm_min > 60 THEN idle_cost_usd * (1 - 30.0 / autoterm_min)
              ELSE GREATEST(idle_cost_usd - active_days * 0.5 * avg_hour_cost_usd, 0)
         END AS saving_usd
  FROM per_cluster
)
SELECT
  '1 Auto-termination off or over 60 min'                                AS waste_item,
  s.workspace_id,
  COALESCE(w.workspace_name, concat('workspace ', s.workspace_id))       AS workspace_name,
  'All-purpose cluster'                                                  AS resource_type,
  s.cluster_id                                                           AS resource_id,
  s.cluster_name                                                         AS resource_name,
  s.owned_by                                                             AS owner,
  concat('Auto-termination ',
         CASE WHEN s.autoterm_min = 0 THEN 'off' ELSE concat(CAST(s.autoterm_min AS STRING), ' min') END,
         '; ', CAST(s.idle_hours AS STRING), ' idle of ', CAST(s.billed_hours AS STRING), ' billed hours')
                                                                         AS finding,
  concat('Set auto-termination to 30 min. Enforce it with a compute policy: ',
         '"autotermination_minutes": {"type": "range", "minValue": 10, "maxValue": 60, "defaultValue": 30}')
                                                                         AS recommended_fix,
  ROUND(s.cost_usd, 2)                                                   AS affected_cost_usd,
  ROUND(s.idle_cost_usd, 2)                                              AS estimated_waste_usd,
  ROUND(s.saving_usd * 30.0 / (datediff(:end_date, :start_date) + 1), 2) AS estimated_monthly_saving_usd,
  'Idle-hour cost x (1 - 30/setting); setting off: idle cost minus 30 min per active day'
                                                                         AS saving_basis,
  s.autoterm_min                                                         AS auto_termination_minutes,
  s.billed_hours,
  s.idle_hours
FROM scored AS s
LEFT JOIN system.access.workspaces_latest AS w ON w.workspace_id = s.workspace_id
ORDER BY estimated_monthly_saving_usd DESC, affected_cost_usd DESC;
