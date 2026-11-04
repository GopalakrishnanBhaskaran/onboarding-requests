-- =============================================================================
-- Dataset 09  cost_by_tag              Page 3 Cost by user        Visual: bar + table
-- Chargeback view: cost per cost-center tag value and owner tag value.
--
-- custom_tags (Learn): "tags applied to the compute resource associated with
-- the usage", including tags from serverless usage policies.
-- Tag keys are matched case-insensitively after removing everything except
-- letters and digits, so CostCenter, cost_center, COST-CENTER all match.
--   EDIT HERE if PEST uses other keys: the two arrays in tag_keys below.
--
-- Tables  system.billing.usage, system.billing.list_prices (global).
-- Params  :start_date, :end_date (DATE).
-- =============================================================================
WITH tag_keys AS (
  SELECT
    array('costcenter', 'costcentre')                     AS cost_center_keys,  -- EDIT HERE
    array('owner', 'businessowner', 'technicalowner')     AS owner_keys         -- EDIT HERE
),
priced AS (
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
tagged AS (
  SELECT
    p.workspace_id,
    p.list_cost_usd,
    NULLIF(trim(try_element_at(map_values(map_filter(p.custom_tags,
      (k, v) -> array_contains(t.cost_center_keys, lower(regexp_replace(k, '[^A-Za-z0-9]', ''))))), 1)), '') AS cost_center,
    NULLIF(trim(try_element_at(map_values(map_filter(p.custom_tags,
      (k, v) -> array_contains(t.owner_keys, lower(regexp_replace(k, '[^A-Za-z0-9]', ''))))), 1)), '')       AS owner_tag
  FROM priced AS p
  CROSS JOIN tag_keys AS t
)
SELECT
  COALESCE(cost_center, '(no cost-center tag)')                     AS cost_center,
  COALESCE(owner_tag, '(no owner tag)')                             AS owner_tag,
  ROUND(SUM(list_cost_usd), 2)                                      AS cost_usd,
  COUNT(DISTINCT workspace_id)                                      AS workspaces,
  ROUND(try_divide(SUM(list_cost_usd), SUM(SUM(list_cost_usd)) OVER ()) * 100, 1) AS share_of_total_pct
FROM tagged
GROUP BY COALESCE(cost_center, '(no cost-center tag)'), COALESCE(owner_tag, '(no owner tag)')
ORDER BY cost_usd DESC;
