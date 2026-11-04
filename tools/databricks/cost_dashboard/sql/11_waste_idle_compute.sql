-- =============================================================================
-- Dataset 11  waste_idle_compute       Page 4 Waste               Visual: table (+ counter)
-- Waste item 2: clusters and SQL warehouses billed while idle or nearly idle.
--
-- Clusters (classic all-purpose, and classic job compute rolled up per job):
--   idle hour = busiest node under 5 % CPU          -> waste = 100 % of that hour
--   low hour  = average node under 20 % CPU (not idle) -> waste = 50 % of that hour
--               (right-sizing estimate: half the nodes or a smaller node type)
-- SQL warehouses: idle hour = billed hour in which no statement ran on the
--   warehouse (system.query.history)                 -> waste = 100 % of that hour
--   Only warehouses in this region's system.compute.warehouses are judged.
-- Resources flagged by item 1 (auto-termination) or item 3 (auto-stop) are left
-- out here, so a cost is counted once on the Recommendations page.
-- Monthly saving = period waste x 30 / days in period.
--
-- Tables  system.compute.clusters, system.compute.node_timeline,
--         system.compute.warehouses, system.query.history (regional),
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
-- Same rule as dataset 10 (item 1); these clusters are reported there.
autoterm_flagged AS (
  SELECT workspace_id, cluster_id
  FROM clusters_latest
  WHERE cluster_source IN ('UI', 'API')
    AND delete_time IS NULL
    AND (auto_termination_minutes IS NULL OR auto_termination_minutes = 0 OR auto_termination_minutes > 60)
),
warehouses_latest AS (
  SELECT *
  FROM (SELECT wh.*, ROW_NUMBER() OVER (PARTITION BY wh.workspace_id, wh.warehouse_id ORDER BY wh.change_time DESC) AS rn
        FROM system.compute.warehouses AS wh) AS t
  WHERE rn = 1
),
-- Same rule as dataset 12 (item 3); these warehouses are reported there.
autostop_flagged AS (
  SELECT workspace_id, warehouse_id
  FROM warehouses_latest
  WHERE delete_time IS NULL
    AND (auto_stop_minutes = 0
         OR auto_stop_minutes > CASE WHEN warehouse_type = 'SERVERLESS' THEN 10 ELSE 45 END)
),
jobs_latest AS (
  SELECT workspace_id, job_id, name, run_as_user_name, creator_user_name
  FROM (SELECT j.*, ROW_NUMBER() OVER (PARTITION BY j.workspace_id, j.job_id ORDER BY j.change_time DESC) AS rn
        FROM system.lakeflow.jobs AS j) AS t
  WHERE rn = 1
),
-- ---------- clusters ----------
cluster_hour_cost AS (
  SELECT
    workspace_id,
    usage_metadata.cluster_id AS cluster_id,
    CASE WHEN billing_origin_product = 'JOBS' THEN 'Job (classic job compute)' ELSE 'All-purpose cluster' END AS resource_type,
    CASE WHEN billing_origin_product = 'JOBS' THEN usage_metadata.job_id ELSE usage_metadata.cluster_id END AS resource_id,
    date_trunc('HOUR', usage_start_time) AS usage_hour,
    SUM(list_cost_usd) AS cost_usd
  FROM priced
  WHERE usage_metadata.cluster_id IS NOT NULL
    AND (billing_origin_product = 'ALL_PURPOSE'
         OR (billing_origin_product = 'JOBS' AND usage_metadata.job_id IS NOT NULL))
  GROUP BY 1, 2, 3, 4, 5
),
node_hour AS (
  SELECT workspace_id, cluster_id, instance_id, date_trunc('HOUR', start_time) AS usage_hour,
         AVG(cpu_user_percent + cpu_system_percent) AS node_cpu_pct
  FROM system.compute.node_timeline
  WHERE start_time >= :start_date AND start_time < date_add(:end_date, 1)
  GROUP BY workspace_id, cluster_id, instance_id, date_trunc('HOUR', start_time)
),
cluster_hour_cpu AS (
  SELECT workspace_id, cluster_id, usage_hour,
         MAX(node_cpu_pct) AS max_node_cpu_pct,
         AVG(node_cpu_pct) AS avg_node_cpu_pct
  FROM node_hour
  GROUP BY workspace_id, cluster_id, usage_hour
),
cluster_hours AS (
  SELECT
    h.*,
    c.avg_node_cpu_pct,
    CASE WHEN c.max_node_cpu_pct IS NULL THEN 'unknown'
         WHEN c.max_node_cpu_pct < 5     THEN 'idle'   -- EDIT HERE: idle CPU threshold (%)
         WHEN c.avg_node_cpu_pct < 20    THEN 'low'    -- EDIT HERE: low-activity CPU threshold (%)
         ELSE 'busy' END AS activity
  FROM cluster_hour_cost AS h
  LEFT JOIN cluster_hour_cpu AS c
    ON c.workspace_id = h.workspace_id AND c.cluster_id = h.cluster_id AND c.usage_hour = h.usage_hour
  LEFT ANTI JOIN autoterm_flagged AS f
    ON h.resource_type = 'All-purpose cluster' AND f.workspace_id = h.workspace_id AND f.cluster_id = h.cluster_id
),
cluster_resources AS (
  SELECT
    workspace_id, resource_type, resource_id,
    SUM(cost_usd)                                                         AS cost_usd,
    COUNT(*)                                                              AS billed_hours,
    SUM(CASE WHEN activity = 'idle' THEN 1 ELSE 0 END)                    AS idle_hours,
    SUM(CASE WHEN activity = 'low'  THEN 1 ELSE 0 END)                    AS low_hours,
    SUM(CASE WHEN activity = 'idle' THEN cost_usd ELSE 0 END)             AS idle_cost_usd,
    SUM(CASE WHEN activity = 'low'  THEN cost_usd ELSE 0 END)             AS low_cost_usd,
    AVG(avg_node_cpu_pct)                                                 AS avg_cpu_pct
  FROM cluster_hours
  GROUP BY workspace_id, resource_type, resource_id
),
-- ---------- SQL warehouses ----------
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
  -- Every clock hour in which a statement was running on the warehouse.
  SELECT DISTINCT q.workspace_id, q.compute.warehouse_id AS warehouse_id, busy_hour AS usage_hour
  FROM system.query.history AS q
  LATERAL VIEW explode(sequence(date_trunc('HOUR', q.start_time),
                                date_trunc('HOUR', COALESCE(q.end_time, q.update_time, q.start_time)),
                                INTERVAL 1 HOUR)) hrs AS busy_hour
  WHERE q.compute.type = 'WAREHOUSE'
    AND q.start_time >= date_sub(:start_date, 1) AND q.start_time < date_add(:end_date, 1)
),
warehouse_hours AS (
  -- Only warehouses this region can see: query.history is regional, so a
  -- warehouse of another region would look idle in every hour.
  SELECT h.*, b.usage_hour IS NULL AS is_idle
  FROM warehouse_hour_cost AS h
  JOIN warehouses_latest AS known
    ON known.workspace_id = h.workspace_id AND known.warehouse_id = h.warehouse_id
  LEFT JOIN warehouse_busy_hours AS b
    ON b.workspace_id = h.workspace_id AND b.warehouse_id = h.warehouse_id AND b.usage_hour = h.usage_hour
  LEFT ANTI JOIN autostop_flagged AS f
    ON f.workspace_id = h.workspace_id AND f.warehouse_id = h.warehouse_id
),
warehouse_resources AS (
  SELECT
    workspace_id, warehouse_id,
    max_by(owned_by, usage_hour)                                  AS owned_by,
    SUM(cost_usd)                                                 AS cost_usd,
    COUNT(*)                                                      AS billed_hours,
    SUM(CASE WHEN is_idle THEN 1 ELSE 0 END)                      AS idle_hours,
    SUM(CASE WHEN is_idle THEN cost_usd ELSE 0 END)               AS idle_cost_usd
  FROM warehouse_hours
  GROUP BY workspace_id, warehouse_id
),
-- ---------- one list ----------
items AS (
  SELECT
    r.workspace_id, r.resource_type, r.resource_id,
    COALESCE(c.cluster_name, j.name, r.resource_id)                         AS resource_name,
    COALESCE(c.owned_by, j.run_as_user_name, j.creator_user_name)           AS owner,
    concat(CAST(r.idle_hours AS STRING), ' idle and ', CAST(r.low_hours AS STRING), ' low-CPU of ',
           CAST(r.billed_hours AS STRING), ' billed hours; average CPU ',
           COALESCE(CAST(ROUND(r.avg_cpu_pct, 1) AS STRING), 'n/a'), ' %')    AS finding,
    CASE WHEN r.resource_type = 'All-purpose cluster'
         THEN concat('Lower auto-termination to 15-30 min',
                     CASE WHEN r.low_hours > 0 THEN '; right-size: fewer workers or a smaller node type, autoscaling with a low min_workers' ELSE '' END,
                     '; run scheduled work as jobs on job compute')
         ELSE concat('Right-size the job cluster (fewer workers or a smaller node type, autoscaling)',
                     ' or move the job to serverless jobs compute')
    END                                                                       AS recommended_fix,
    r.cost_usd                                                                AS affected_cost_usd,
    r.idle_cost_usd + 0.5 * r.low_cost_usd                                    AS waste_usd,
    'Idle hours 100 % + low-CPU hours 50 % of their cost'                     AS saving_basis,
    r.billed_hours, r.idle_hours, r.low_hours
  FROM cluster_resources AS r
  LEFT JOIN clusters_latest AS c
    ON r.resource_type = 'All-purpose cluster' AND c.workspace_id = r.workspace_id AND c.cluster_id = r.resource_id
  LEFT JOIN jobs_latest AS j
    ON r.resource_type = 'Job (classic job compute)' AND j.workspace_id = r.workspace_id AND j.job_id = r.resource_id
  UNION ALL
  SELECT
    r.workspace_id, 'SQL warehouse', r.warehouse_id,
    COALESCE(wl.warehouse_name, r.warehouse_id),
    r.owned_by,
    concat(CAST(r.idle_hours AS STRING), ' of ', CAST(r.billed_hours AS STRING),
           ' billed hours with no statement; auto-stop ',
           COALESCE(CAST(wl.auto_stop_minutes AS STRING), 'n/a'), ' min, clusters ',
           COALESCE(CAST(wl.min_clusters AS STRING), '?'), '-', COALESCE(CAST(wl.max_clusters AS STRING), '?')),
    concat('Keep min clusters at 1, merge little-used warehouses',
           CASE WHEN wl.warehouse_type = 'SERVERLESS' THEN ', consider auto-stop 5 min'
                ELSE ', upgrade to serverless (auto-stop 10 min)' END),
    r.cost_usd,
    r.idle_cost_usd,
    'Billed hours with no statement: 100 % of their cost',
    r.billed_hours, r.idle_hours, CAST(0 AS BIGINT)
  FROM warehouse_resources AS r
  LEFT JOIN warehouses_latest AS wl
    ON wl.workspace_id = r.workspace_id AND wl.warehouse_id = r.warehouse_id
)
SELECT
  '2 Idle or low-activity compute'                                         AS waste_item,
  i.workspace_id,
  COALESCE(w.workspace_name, concat('workspace ', i.workspace_id))         AS workspace_name,
  i.resource_type,
  i.resource_id,
  i.resource_name,
  i.owner,
  i.finding,
  i.recommended_fix,
  ROUND(i.affected_cost_usd, 2)                                            AS affected_cost_usd,
  ROUND(i.waste_usd, 2)                                                    AS estimated_waste_usd,
  ROUND(i.waste_usd * 30.0 / (datediff(:end_date, :start_date) + 1), 2)    AS estimated_monthly_saving_usd,
  i.saving_basis,
  i.billed_hours,
  i.idle_hours,
  i.low_hours
FROM items AS i
LEFT JOIN system.access.workspaces_latest AS w ON w.workspace_id = i.workspace_id
WHERE i.waste_usd > 0
ORDER BY estimated_monthly_saving_usd DESC;
