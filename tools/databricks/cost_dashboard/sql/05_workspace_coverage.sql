-- =============================================================================
-- Dataset 05  workspace_coverage       Page 1 Cost by workspace   Visual: table
-- Which workspaces the REGIONAL tables can see from this dashboard's workspace.
-- Billing (pages 1-3 cost) is global. Names and the waste checks (pages 2, 4,
-- 5) come from regional tables: "System tables contain operational data for
-- all workspaces in your account deployed within the same cloud region."
-- A workspace marked "No" is in another region (or has no compute records):
-- run a copy of this dashboard from a workspace in that region.
--
-- Tables  system.billing.usage, system.billing.list_prices, system.access.workspaces_latest
--         (global); system.compute.clusters, system.compute.warehouses,
--         system.lakeflow.jobs, system.query.history (regional).
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
by_workspace AS (
  SELECT
    workspace_id,
    SUM(list_cost_usd) AS cost_usd,
    SUM(CASE WHEN product_features.is_serverless THEN 0 ELSE list_cost_usd END) AS non_serverless_cost_usd
  FROM priced
  WHERE workspace_id IS NOT NULL
  GROUP BY workspace_id
),
regional_seen AS (
  SELECT DISTINCT workspace_id FROM system.compute.clusters
  UNION
  SELECT DISTINCT workspace_id FROM system.compute.warehouses
  UNION
  SELECT DISTINCT workspace_id FROM system.lakeflow.jobs
  UNION
  SELECT DISTINCT workspace_id FROM system.query.history
  WHERE start_time >= :start_date AND start_time < date_add(:end_date, 1)
)
SELECT
  COALESCE(w.workspace_name, concat('workspace ', b.workspace_id))       AS workspace_name,
  b.workspace_id,
  w.status                                                               AS workspace_status,
  ROUND(b.cost_usd, 2)                                                   AS cost_usd,
  CASE WHEN r.workspace_id IS NOT NULL THEN 'Yes' ELSE 'No' END          AS regional_data_visible,
  CASE WHEN r.workspace_id IS NOT NULL
       THEN 'Names and waste checks cover this workspace'
       ELSE 'Cost is shown; names and waste checks need a dashboard copy in this workspace''s region'
  END                                                                    AS coverage_note
FROM by_workspace AS b
LEFT JOIN regional_seen AS r
  ON r.workspace_id = b.workspace_id
LEFT JOIN system.access.workspaces_latest AS w
  ON w.workspace_id = b.workspace_id
ORDER BY regional_data_visible, cost_usd DESC;
