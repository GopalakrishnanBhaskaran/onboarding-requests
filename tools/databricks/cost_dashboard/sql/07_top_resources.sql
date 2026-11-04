-- =============================================================================
-- Dataset 07  top_resources            Page 2 Cost by resource    Visual: table (+ type filter)
-- Top 20 by cost of each resource type: all-purpose clusters, SQL warehouses,
-- jobs (job compute and serverless), Lakeflow pipelines, serving endpoints.
--
-- Tables  system.billing.usage, system.billing.list_prices, system.access.workspaces_latest
--         (global); system.compute.clusters, system.compute.warehouses,
--         system.lakeflow.jobs, system.lakeflow.pipelines (regional: names only;
--         a resource in another region shows its id).
-- Params  :start_date, :end_date (DATE).
-- Notes   usage_metadata.job_id is filled for job compute and serverless jobs,
--         never for jobs on all-purpose compute (those cost under the cluster).
--         Job compute usage also carries a cluster_id; it is ranked as a job,
--         not as a cluster (clusters = billing_origin_product ALL_PURPOSE).
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
resource_cost AS (
  SELECT 'All-purpose cluster' AS resource_type, workspace_id, usage_metadata.cluster_id AS resource_id,
         CAST(NULL AS STRING) AS billed_name,
         max_by(identity_metadata.run_as, usage_end_time) AS billed_identity,
         SUM(list_cost_usd) AS cost_usd, SUM(usage_quantity) AS usage_quantity
  FROM priced
  WHERE billing_origin_product = 'ALL_PURPOSE' AND usage_metadata.cluster_id IS NOT NULL
  GROUP BY workspace_id, usage_metadata.cluster_id
  UNION ALL
  SELECT 'SQL warehouse', workspace_id, usage_metadata.warehouse_id,
         CAST(NULL AS STRING),
         max_by(identity_metadata.owned_by, usage_end_time),
         SUM(list_cost_usd), SUM(usage_quantity)
  FROM priced
  WHERE usage_metadata.warehouse_id IS NOT NULL
  GROUP BY workspace_id, usage_metadata.warehouse_id
  UNION ALL
  SELECT 'Job', workspace_id, usage_metadata.job_id,
         max_by(usage_metadata.job_name, usage_end_time),
         max_by(identity_metadata.run_as, usage_end_time),
         SUM(list_cost_usd), SUM(usage_quantity)
  FROM priced
  WHERE usage_metadata.job_id IS NOT NULL
  GROUP BY workspace_id, usage_metadata.job_id
  UNION ALL
  SELECT 'Lakeflow pipeline', workspace_id, usage_metadata.dlt_pipeline_id,
         CAST(NULL AS STRING),
         max_by(identity_metadata.run_as, usage_end_time),
         SUM(list_cost_usd), SUM(usage_quantity)
  FROM priced
  WHERE usage_metadata.dlt_pipeline_id IS NOT NULL
  GROUP BY workspace_id, usage_metadata.dlt_pipeline_id
  UNION ALL
  SELECT 'Serving endpoint', workspace_id, usage_metadata.endpoint_name,
         max_by(usage_metadata.endpoint_name, usage_end_time),
         max_by(identity_metadata.run_as, usage_end_time),
         SUM(list_cost_usd), SUM(usage_quantity)
  FROM priced
  WHERE billing_origin_product = 'MODEL_SERVING' AND usage_metadata.endpoint_name IS NOT NULL
  GROUP BY workspace_id, usage_metadata.endpoint_name
),
ranked AS (
  SELECT *,
         ROW_NUMBER() OVER (PARTITION BY resource_type ORDER BY COALESCE(cost_usd, 0) DESC) AS type_rank,
         SUM(cost_usd) OVER (PARTITION BY resource_type) AS type_total_usd
  FROM resource_cost
),
-- Latest row of each slowly changing dimension table (Learn: take the row with
-- the most recent change_time per entity key).
clusters_latest AS (
  SELECT workspace_id, cluster_id, cluster_name, owned_by
  FROM (SELECT c.*, ROW_NUMBER() OVER (PARTITION BY c.workspace_id, c.cluster_id ORDER BY c.change_time DESC) AS rn
        FROM system.compute.clusters AS c) AS t
  WHERE rn = 1
),
warehouses_latest AS (
  SELECT workspace_id, warehouse_id, warehouse_name, warehouse_type, warehouse_size
  FROM (SELECT wh.*, ROW_NUMBER() OVER (PARTITION BY wh.workspace_id, wh.warehouse_id ORDER BY wh.change_time DESC) AS rn
        FROM system.compute.warehouses AS wh) AS t
  WHERE rn = 1
),
jobs_latest AS (
  SELECT workspace_id, job_id, name, run_as_user_name, creator_user_name
  FROM (SELECT j.*, ROW_NUMBER() OVER (PARTITION BY j.workspace_id, j.job_id ORDER BY j.change_time DESC) AS rn
        FROM system.lakeflow.jobs AS j) AS t
  WHERE rn = 1
),
pipelines_latest AS (
  SELECT workspace_id, pipeline_id, name, run_as, created_by
  FROM (SELECT pl.*, ROW_NUMBER() OVER (PARTITION BY pl.workspace_id, pl.pipeline_id ORDER BY pl.change_time DESC) AS rn
        FROM system.lakeflow.pipelines AS pl) AS t
  WHERE rn = 1
)
SELECT
  r.resource_type,
  r.type_rank,
  COALESCE(w.workspace_name, concat('workspace ', r.workspace_id))          AS workspace_name,
  r.workspace_id,
  r.resource_id,
  COALESCE(c.cluster_name, wh.warehouse_name, j.name, pl.name, r.billed_name, r.resource_id) AS resource_name,
  CASE WHEN r.resource_type = 'SQL warehouse'
       THEN concat(wh.warehouse_type, ' ', wh.warehouse_size) END            AS warehouse_config,
  COALESCE(c.owned_by, j.run_as_user_name, pl.run_as, r.billed_identity,
           j.creator_user_name, pl.created_by)                               AS owner_or_run_as,
  ROUND(r.cost_usd, 2)                                                       AS cost_usd,
  ROUND(CAST(r.usage_quantity AS DOUBLE), 2)                                 AS usage_quantity,
  ROUND(try_divide(r.cost_usd, r.type_total_usd) * 100, 1)                   AS share_of_type_pct,
  CASE WHEN r.resource_type = 'Job'               THEN concat(w.workspace_url, '/jobs/', r.resource_id)
       WHEN r.resource_type = 'Lakeflow pipeline' THEN concat(w.workspace_url, '/pipelines/', r.resource_id)
  END                                                                        AS resource_url
FROM ranked AS r
LEFT JOIN system.access.workspaces_latest AS w ON w.workspace_id = r.workspace_id
LEFT JOIN clusters_latest AS c
  ON r.resource_type = 'All-purpose cluster' AND c.workspace_id = r.workspace_id AND c.cluster_id = r.resource_id
LEFT JOIN warehouses_latest AS wh
  ON r.resource_type = 'SQL warehouse' AND wh.workspace_id = r.workspace_id AND wh.warehouse_id = r.resource_id
LEFT JOIN jobs_latest AS j
  ON r.resource_type = 'Job' AND j.workspace_id = r.workspace_id AND j.job_id = r.resource_id
LEFT JOIN pipelines_latest AS pl
  ON r.resource_type = 'Lakeflow pipeline' AND pl.workspace_id = r.workspace_id AND pl.pipeline_id = r.resource_id
WHERE r.type_rank <= 20
ORDER BY r.resource_type, r.type_rank;
