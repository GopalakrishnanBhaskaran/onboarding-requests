-- =============================================================================
-- Dataset 01  kpi_summary              Page 1 Cost by workspace   Visual: counters
-- Total list cost of the selected period, the previous period of the same
-- length, the change, the daily average and a 30-day run rate.
--
-- Tables  system.billing.usage, system.billing.list_prices (both GLOBAL: every
--         workspace in the account, from any region).
-- Params  :start_date, :end_date (DATE). Dashboard defaults now-30d/d and now/d.
-- Cost    usage_quantity x pricing.effective_list.default (USD list price).
--         Not your discounted rate; Azure VM cost of classic compute is billed
--         by Azure separately and is not in system.billing.usage.
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
  -- Selected period plus the previous period of the same length.
  WHERE u.usage_date BETWEEN date_sub(:start_date, datediff(:end_date, :start_date) + 1) AND :end_date
),
totals AS (
  SELECT
    SUM(CASE WHEN usage_date >= :start_date THEN list_cost_usd ELSE 0 END)                         AS cost_usd,
    SUM(CASE WHEN usage_date <  :start_date THEN list_cost_usd ELSE 0 END)                         AS previous_cost_usd,
    SUM(CASE WHEN usage_date >= :start_date AND usage_unit = 'DBU' THEN usage_quantity ELSE 0 END) AS dbus,
    COUNT(DISTINCT CASE WHEN usage_date >= :start_date THEN workspace_id END)                      AS workspaces_with_spend,
    -- Usage with no matching list price (cost shows as 0). Should stay 0.
    SUM(CASE WHEN usage_date >= :start_date AND list_cost_usd IS NULL THEN usage_quantity ELSE 0 END) AS unpriced_quantity
  FROM priced
)
SELECT
  ROUND(cost_usd, 2)                                                       AS cost_usd,
  ROUND(previous_cost_usd, 2)                                              AS previous_period_cost_usd,
  ROUND(try_divide(cost_usd - previous_cost_usd, previous_cost_usd) * 100, 1) AS change_pct,
  ROUND(cost_usd / (datediff(:end_date, :start_date) + 1), 2)              AS avg_daily_cost_usd,
  ROUND(cost_usd / (datediff(:end_date, :start_date) + 1) * 30, 2)         AS run_rate_30d_usd,
  ROUND(CAST(dbus AS DOUBLE), 1)                                           AS dbus,
  workspaces_with_spend,
  ROUND(CAST(unpriced_quantity AS DOUBLE), 2)                              AS unpriced_quantity
FROM totals;
