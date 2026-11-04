-- =============================================================================
-- Dataset 06  cost_by_product_sku      Page 2 Cost by resource    Visual: bar + table
-- Ranks billing_origin_product x sku_name, grouped into a compute category
-- (all-purpose, jobs, SQL warehouse, serverless, DLT, model serving, ...).
--
-- Tables  system.billing.usage, system.billing.list_prices (global).
-- Params  :start_date, :end_date (DATE).
-- Note    product_features.is_serverless is true/false only where you can
--         choose serverless or classic, otherwise NULL (Learn).
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
categorised AS (
  SELECT
    *,
    CASE
      WHEN billing_origin_product = 'ALL_PURPOSE'                       THEN 'All-purpose compute (classic)'
      WHEN billing_origin_product = 'INTERACTIVE'                       THEN 'Serverless notebooks'
      WHEN billing_origin_product = 'JOBS'
           AND COALESCE(product_features.is_serverless, FALSE)          THEN 'Jobs (serverless)'
      WHEN billing_origin_product = 'JOBS'                              THEN 'Jobs compute (classic)'
      WHEN billing_origin_product = 'SQL'
           AND (COALESCE(product_features.is_serverless, FALSE)
                OR sku_name LIKE '%SERVERLESS%')                        THEN 'SQL warehouse (serverless)'
      WHEN billing_origin_product = 'SQL'                               THEN 'SQL warehouse (pro/classic)'
      WHEN billing_origin_product = 'DLT'
           AND COALESCE(product_features.is_serverless, FALSE)          THEN 'Lakeflow pipelines / DLT (serverless)'
      WHEN billing_origin_product = 'DLT'                               THEN 'Lakeflow pipelines / DLT (classic)'
      WHEN billing_origin_product = 'MODEL_SERVING'                     THEN 'Model serving'
      ELSE concat('Other: ', COALESCE(billing_origin_product, 'unknown'))
    END AS compute_category
  FROM priced
),
by_sku AS (
  SELECT
    compute_category,
    billing_origin_product,
    sku_name,
    usage_unit,
    SUM(list_cost_usd)  AS cost_usd,
    SUM(usage_quantity) AS usage_quantity,
    COUNT(DISTINCT workspace_id) AS workspaces
  FROM categorised
  GROUP BY compute_category, billing_origin_product, sku_name, usage_unit
)
SELECT
  RANK() OVER (ORDER BY COALESCE(cost_usd, 0) DESC)                 AS cost_rank,
  compute_category,
  billing_origin_product,
  sku_name,
  usage_unit,
  ROUND(cost_usd, 2)                                                AS cost_usd,
  ROUND(CAST(usage_quantity AS DOUBLE), 2)                          AS usage_quantity,
  workspaces,
  ROUND(try_divide(cost_usd, SUM(cost_usd) OVER ()) * 100, 1)       AS share_of_total_pct
FROM by_sku
ORDER BY cost_rank;
