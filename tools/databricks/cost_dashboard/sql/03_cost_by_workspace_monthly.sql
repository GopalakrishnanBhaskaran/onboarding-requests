-- =============================================================================
-- Dataset 03  cost_by_workspace_monthly  Page 1 Cost by workspace  Visual: stacked bar + table
-- Month-over-month trend: the 6 calendar months up to :end_date (the last one
-- is month-to-date), per workspace, with the previous month and the change.
-- Uses only :end_date, so the trend always shows 6 months.
--
-- Tables  system.billing.usage, system.billing.list_prices (global),
--         system.access.workspaces_latest (global, Public Preview).
-- Params  :end_date (DATE).
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
  WHERE u.usage_date BETWEEN trunc(add_months(:end_date, -5), 'MM') AND :end_date
),
monthly AS (
  SELECT trunc(usage_date, 'MM') AS usage_month, workspace_id, SUM(list_cost_usd) AS cost_usd
  FROM priced
  GROUP BY trunc(usage_date, 'MM'), workspace_id
),
-- Every month x every workspace, so a month without usage counts as 0 and
-- the previous-month value always comes from the calendar month before.
months AS (
  SELECT explode(sequence(trunc(add_months(:end_date, -5), 'MM'), trunc(:end_date, 'MM'), INTERVAL 1 MONTH)) AS usage_month
),
grid AS (
  SELECT m.usage_month, ws.workspace_id
  FROM months AS m
  CROSS JOIN (SELECT DISTINCT workspace_id FROM monthly) AS ws
),
filled AS (
  SELECT
    g.usage_month,
    g.workspace_id,
    COALESCE(mo.cost_usd, 0) AS cost_usd,
    LAG(COALESCE(mo.cost_usd, 0)) OVER (PARTITION BY g.workspace_id ORDER BY g.usage_month) AS previous_month_cost_usd
  FROM grid AS g
  LEFT JOIN monthly AS mo
    ON mo.usage_month = g.usage_month
   AND mo.workspace_id <=> g.workspace_id
)
SELECT
  f.usage_month,
  date_format(f.usage_month, 'yyyy-MM')                                   AS month_label,
  COALESCE(w.workspace_name,
           CASE WHEN f.workspace_id IS NULL THEN '(account level)'
                ELSE concat('workspace ', f.workspace_id) END)             AS workspace_name,
  f.workspace_id,
  ROUND(f.cost_usd, 2)                                                    AS cost_usd,
  ROUND(f.previous_month_cost_usd, 2)                                     AS previous_month_cost_usd,
  ROUND(try_divide(f.cost_usd - f.previous_month_cost_usd, f.previous_month_cost_usd) * 100, 1) AS mom_change_pct,
  f.usage_month = trunc(:end_date, 'MM')                                  AS is_month_to_date
FROM filled AS f
LEFT JOIN system.access.workspaces_latest AS w
  ON w.workspace_id = f.workspace_id
ORDER BY f.usage_month, cost_usd DESC;
