-- =============================================================================
-- Dataset 04  cost_daily_by_product    Page 1 Cost by workspace   Visual: stacked area/line
-- Daily list cost per billing_origin_product in the selected period.
--
-- Tables  system.billing.usage, system.billing.list_prices (global).
-- Params  :start_date, :end_date (DATE).
-- Note    usage_date is a UTC date. Billing records are "typically available
--         within 12 hours", so the last day is incomplete.
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
)
SELECT
  usage_date,
  billing_origin_product,
  ROUND(SUM(list_cost_usd), 2) AS cost_usd
FROM priced
GROUP BY usage_date, billing_origin_product
ORDER BY usage_date, billing_origin_product;
