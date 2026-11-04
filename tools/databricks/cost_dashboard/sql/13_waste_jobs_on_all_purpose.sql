-- =============================================================================
-- Dataset 13  waste_jobs_on_all_purpose  Page 4 Waste             Visual: table (+ counter)
-- Waste item 4: jobs whose tasks ran on all-purpose compute instead of job compute.
--
-- Found with the Learn pattern "Find jobs running on all-purpose compute":
-- job_task_run_timeline.compute_ids joined to clusters with cluster_source UI/API.
-- Cost: billing.usage has no job_id for jobs on all-purpose compute, so the
-- cluster's hourly cost is shared out by task minutes:
--   job share of an hour = job task minutes / greatest(60, all task minutes in that hour)
-- Learn: this "is not possible with 100% accuracy" on shared clusters; it is an estimate.
-- Estimated waste = attributed cost x (1 - jobs price / all-purpose price), both
-- from list_prices; the jobs SKU is the all-purpose SKU name with ALL_PURPOSE
-- replaced by JOBS (for example PREMIUM_ALL_PURPOSE_COMPUTE -> PREMIUM_JOBS_COMPUTE).
-- The VM cost is the same on both, so only the DBU price changes.
-- Monthly saving = period waste x 30 / days in period.
--
-- Tables  system.lakeflow.job_task_run_timeline, system.lakeflow.jobs,
--         system.compute.clusters (regional), system.billing.usage,
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
-- The same usage priced as job compute: ALL_PURPOSE -> JOBS in the SKU name.
priced_as_jobs AS (
  SELECT
    pr.workspace_id,
    pr.usage_metadata.cluster_id                         AS cluster_id,
    date_trunc('HOUR', pr.usage_start_time)              AS usage_hour,
    SUM(pr.list_cost_usd)                                AS cost_usd,
    SUM(CAST(pr.usage_quantity AS DOUBLE)
        * CAST(COALESCE(j.pricing.effective_list.default, j.pricing.default) AS DOUBLE)) AS cost_at_jobs_price_usd,
    COUNT(j.sku_name) = COUNT(*)                         AS jobs_price_found
  FROM priced AS pr
  LEFT JOIN system.billing.list_prices AS j
    ON  j.sku_name = replace(pr.sku_name, 'ALL_PURPOSE', 'JOBS')
    AND j.cloud = pr.cloud
    AND j.usage_unit = pr.usage_unit
    AND j.currency_code = 'USD'
    AND pr.usage_end_time >= j.price_start_time
    AND (j.price_end_time IS NULL OR pr.usage_end_time < j.price_end_time)
  WHERE pr.billing_origin_product = 'ALL_PURPOSE' AND pr.usage_metadata.cluster_id IS NOT NULL
  GROUP BY pr.workspace_id, pr.usage_metadata.cluster_id, date_trunc('HOUR', pr.usage_start_time)
),
all_purpose_clusters AS (
  SELECT workspace_id, cluster_id, cluster_name, owned_by
  FROM (SELECT c.*, ROW_NUMBER() OVER (PARTITION BY c.workspace_id, c.cluster_id ORDER BY c.change_time DESC) AS rn
        FROM system.compute.clusters AS c) AS t
  WHERE rn = 1 AND cluster_source IN ('UI', 'API')
),
task_slices AS (
  SELECT t.workspace_id, t.job_id, t.run_id AS task_run_id, cid AS cluster_id,
         t.period_start_time, t.period_end_time
  FROM system.lakeflow.job_task_run_timeline AS t
  LATERAL VIEW explode(t.compute_ids) cids AS cid
  WHERE t.period_start_time >= date_sub(:start_date, 1) AND t.period_start_time < date_add(:end_date, 1)
),
ap_task_hours AS (
  -- Split every task slice by clock hour (works for both timeline slicing logics).
  SELECT s.workspace_id, s.job_id, s.cluster_id, h AS usage_hour,
         GREATEST(0, timestampdiff(SECOND, GREATEST(s.period_start_time, h),
                                   LEAST(s.period_end_time, h + INTERVAL 1 HOUR))) / 60.0 AS task_minutes
  FROM task_slices AS s
  JOIN all_purpose_clusters AS c
    ON c.workspace_id = s.workspace_id AND c.cluster_id = s.cluster_id
  LATERAL VIEW explode(sequence(date_trunc('HOUR', s.period_start_time),
                                date_trunc('HOUR', s.period_end_time), INTERVAL 1 HOUR)) hrs AS h
),
cluster_hour_minutes AS (
  SELECT workspace_id, cluster_id, usage_hour, SUM(task_minutes) AS all_task_minutes
  FROM ap_task_hours
  GROUP BY workspace_id, cluster_id, usage_hour
),
job_hour_share AS (
  SELECT a.workspace_id, a.job_id, a.cluster_id, a.usage_hour,
         SUM(a.task_minutes) AS job_minutes,
         MAX(m.all_task_minutes) AS all_task_minutes
  FROM ap_task_hours AS a
  JOIN cluster_hour_minutes AS m
    ON m.workspace_id = a.workspace_id AND m.cluster_id = a.cluster_id AND m.usage_hour = a.usage_hour
  GROUP BY a.workspace_id, a.job_id, a.cluster_id, a.usage_hour
),
job_cost AS (
  SELECT
    s.workspace_id, s.job_id,
    SUM(s.job_minutes) / 60.0                                                         AS task_hours,
    SUM(pj.cost_usd * s.job_minutes / GREATEST(60.0, s.all_task_minutes))            AS attributed_cost_usd,
    SUM((pj.cost_usd - pj.cost_at_jobs_price_usd) * s.job_minutes / GREATEST(60.0, s.all_task_minutes)) AS premium_usd,
    BOOL_AND(pj.jobs_price_found)                                                     AS jobs_price_found,
    array_join(array_sort(collect_set(COALESCE(c.cluster_name, s.cluster_id))), ', ') AS clusters
  FROM job_hour_share AS s
  JOIN priced_as_jobs AS pj
    ON pj.workspace_id = s.workspace_id AND pj.cluster_id = s.cluster_id AND pj.usage_hour = s.usage_hour
  LEFT JOIN all_purpose_clusters AS c
    ON c.workspace_id = s.workspace_id AND c.cluster_id = s.cluster_id
  GROUP BY s.workspace_id, s.job_id
),
jobs_latest AS (
  SELECT workspace_id, job_id, name, run_as_user_name, creator_user_name
  FROM (SELECT j.*, ROW_NUMBER() OVER (PARTITION BY j.workspace_id, j.job_id ORDER BY j.change_time DESC) AS rn
        FROM system.lakeflow.jobs AS j) AS t
  WHERE rn = 1
)
SELECT
  '4 Jobs on all-purpose compute'                                        AS waste_item,
  jc.workspace_id,
  COALESCE(w.workspace_name, concat('workspace ', jc.workspace_id))      AS workspace_name,
  'Job'                                                                  AS resource_type,
  jc.job_id                                                              AS resource_id,
  COALESCE(jl.name, jc.job_id)                                           AS resource_name,
  COALESCE(jl.run_as_user_name, jl.creator_user_name)                    AS owner,
  concat(CAST(ROUND(jc.task_hours, 1) AS STRING), ' task hours on all-purpose cluster(s): ', jc.clusters,
         CASE WHEN jc.jobs_price_found THEN '' ELSE ' (jobs SKU price not found for some hours)' END)
                                                                         AS finding,
  concat('Move the tasks to a job cluster (new job compute) or serverless jobs; block jobs on all-purpose compute ',
         'with a compute policy: "workload_type.clients.jobs": {"type": "fixed", "value": false}')
                                                                         AS recommended_fix,
  ROUND(jc.attributed_cost_usd, 2)                                       AS affected_cost_usd,
  ROUND(COALESCE(jc.premium_usd, 0), 2)                                  AS estimated_waste_usd,
  ROUND(COALESCE(jc.premium_usd, 0) * 30.0 / (datediff(:end_date, :start_date) + 1), 2)
                                                                         AS estimated_monthly_saving_usd,
  'Attributed all-purpose cost minus the same DBUs at the jobs compute list price'
                                                                         AS saving_basis,
  ROUND(jc.task_hours, 1)                                                AS task_hours,
  jc.clusters
FROM job_cost AS jc
LEFT JOIN jobs_latest AS jl ON jl.workspace_id = jc.workspace_id AND jl.job_id = jc.job_id
LEFT JOIN system.access.workspaces_latest AS w ON w.workspace_id = jc.workspace_id
ORDER BY estimated_monthly_saving_usd DESC;
