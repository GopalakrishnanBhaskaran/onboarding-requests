-- =============================================================================
-- Alert query: daily cost of the whole PEST account (all workspaces), USD list price.
-- Use it in a Databricks SQL alert (Alerts > Create alert, or POST /api/2.0/alerts).
-- Condition: cost_usd  >  your daily threshold (static value), first row.
--
-- No parameters: Learn, "Alerts do not support queries with parameters".
-- usage_date is a UTC date; billing records are "typically available within
-- 12 hours", so schedule the alert at 14:00 UTC (19:30 IST) or later to judge
-- the previous UTC day on complete data.
-- Tables system.billing.usage, system.billing.list_prices (global).
-- =============================================================================
WITH priced AS (
  SELECT
    u.usage_date,
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
  WHERE u.usage_date BETWEEN date_sub(current_date(), 8) AND date_sub(current_date(), 1)
),
daily AS (
  SELECT usage_date, SUM(list_cost_usd) AS cost_usd
  FROM priced
  GROUP BY usage_date
)
SELECT
  date_sub(current_date(), 1)                                                     AS usage_date,
  ROUND(COALESCE(MAX(CASE WHEN usage_date = date_sub(current_date(), 1) THEN cost_usd END), 0), 2) AS cost_usd,
  -- Average over all 7 days before yesterday (a day without usage counts as 0).
  ROUND(COALESCE(SUM(CASE WHEN usage_date < date_sub(current_date(), 1) THEN cost_usd END), 0) / 7, 2) AS trailing_7d_avg_usd,
  ROUND(try_divide(
          COALESCE(MAX(CASE WHEN usage_date = date_sub(current_date(), 1) THEN cost_usd END), 0)
          - COALESCE(SUM(CASE WHEN usage_date < date_sub(current_date(), 1) THEN cost_usd END), 0) / 7,
          COALESCE(SUM(CASE WHEN usage_date < date_sub(current_date(), 1) THEN cost_usd END), 0) / 7) * 100, 1) AS change_vs_7d_avg_pct
FROM daily;
