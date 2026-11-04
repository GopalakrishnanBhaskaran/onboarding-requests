-- =============================================================================
-- Dataset 02  cost_by_workspace        Page 1 Cost by workspace   Visual: bar + table
-- Total USD per workspace in the selected period, ranked highest first.
--
-- Tables  system.billing.usage, system.billing.list_prices (global),
--         system.access.workspaces_latest (global, Public Preview: names).
-- Params  :start_date, :end_date (DATE).
-- Note    workspace_id is NULL for account-level usage such as serverless
--         private endpoints (billing_origin_product NETWORKING).
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
    SUM(list_cost_usd)                                            AS cost_usd,
    SUM(CASE WHEN usage_unit = 'DBU' THEN usage_quantity END)     AS dbus,
    SUM(CASE WHEN product_features.is_serverless THEN list_cost_usd ELSE 0 END) AS serverless_cost_usd
  FROM priced
  GROUP BY workspace_id
)
SELECT
  RANK() OVER (ORDER BY COALESCE(b.cost_usd, 0) DESC)                    AS cost_rank,
  COALESCE(w.workspace_name,
           CASE WHEN b.workspace_id IS NULL THEN '(account level)'
                ELSE concat('workspace ', b.workspace_id) END)            AS workspace_name,
  b.workspace_id,
  w.workspace_url,
  ROUND(b.cost_usd, 2)                                                   AS cost_usd,
  ROUND(CAST(b.dbus AS DOUBLE), 1)                                       AS dbus,
  ROUND(try_divide(b.serverless_cost_usd, b.cost_usd) * 100, 1)          AS serverless_share_pct,
  ROUND(try_divide(b.cost_usd, SUM(b.cost_usd) OVER ()) * 100, 1)        AS share_of_total_pct
FROM by_workspace AS b
LEFT JOIN system.access.workspaces_latest AS w
  ON w.workspace_id = b.workspace_id
ORDER BY cost_rank;
