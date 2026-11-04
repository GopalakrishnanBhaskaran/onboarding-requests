-- =============================================================================
-- Dataset 14  unattributed_spend       Page 4 Waste               Visual: table (+ counter)
-- Waste item 5: spend without a cost-center tag or an owner tag, per
-- workspace and compute category. This is unmonitored spend, not waste:
-- estimated_waste_usd and the saving are 0; affected_cost_usd is the USD at risk.
--
-- Tag keys as in dataset 09 (case-insensitive, letters and digits only).
--   EDIT HERE if PEST uses other keys: the two arrays in tag_keys below.
--
-- Tables  system.billing.usage, system.billing.list_prices,
--         system.access.workspaces_latest (global).
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
    CASE
      WHEN p.billing_origin_product = 'ALL_PURPOSE'                                          THEN 'All-purpose compute (classic)'
      WHEN p.billing_origin_product IN ('JOBS', 'DLT') AND COALESCE(p.product_features.is_serverless, FALSE) THEN 'Serverless jobs and pipelines'
      WHEN p.billing_origin_product IN ('JOBS', 'DLT')                                       THEN 'Classic jobs and pipelines'
      WHEN p.billing_origin_product = 'SQL'                                                  THEN 'SQL warehouses'
      WHEN p.billing_origin_product = 'INTERACTIVE'                                          THEN 'Serverless notebooks'
      WHEN p.billing_origin_product = 'MODEL_SERVING'                                        THEN 'Model serving'
      ELSE concat('Other: ', COALESCE(p.billing_origin_product, 'unknown'))
    END AS compute_category,
    NULLIF(trim(try_element_at(map_values(map_filter(p.custom_tags,
      (k, v) -> array_contains(t.cost_center_keys, lower(regexp_replace(k, '[^A-Za-z0-9]', ''))))), 1)), '') AS cost_center,
    NULLIF(trim(try_element_at(map_values(map_filter(p.custom_tags,
      (k, v) -> array_contains(t.owner_keys, lower(regexp_replace(k, '[^A-Za-z0-9]', ''))))), 1)), '')       AS owner_tag
  FROM priced AS p
  CROSS JOIN tag_keys AS t
),
by_category AS (
  SELECT
    workspace_id,
    compute_category,
    SUM(list_cost_usd)                                                              AS cost_usd,
    SUM(CASE WHEN cost_center IS NULL THEN list_cost_usd ELSE 0 END)                AS no_cost_center_usd,
    SUM(CASE WHEN owner_tag  IS NULL THEN list_cost_usd ELSE 0 END)                 AS no_owner_usd,
    SUM(CASE WHEN cost_center IS NULL OR owner_tag IS NULL THEN list_cost_usd ELSE 0 END) AS unattributed_usd
  FROM tagged
  GROUP BY workspace_id, compute_category
)
SELECT
  '5 Unattributed spend (no cost-center or owner tag)'                   AS waste_item,
  b.workspace_id,
  COALESCE(w.workspace_name,
           CASE WHEN b.workspace_id IS NULL THEN '(account level)'
                ELSE concat('workspace ', b.workspace_id) END)            AS workspace_name,
  'Usage category'                                                       AS resource_type,
  concat(COALESCE(b.workspace_id, 'account'), ':', b.compute_category)   AS resource_id,
  b.compute_category                                                     AS resource_name,
  CAST(NULL AS STRING)                                                   AS owner,
  concat(CAST(ROUND(try_divide(b.no_cost_center_usd, b.cost_usd) * 100, 0) AS STRING), ' % without cost-center tag, ',
         CAST(ROUND(try_divide(b.no_owner_usd, b.cost_usd) * 100, 0) AS STRING), ' % without owner tag')
                                                                         AS finding,
  CASE b.compute_category
    WHEN 'All-purpose compute (classic)'  THEN 'Make tags mandatory in every compute policy: "custom_tags.CostCenter": {"type": "allowlist", "values": [...]} and "custom_tags.Owner": {"type": "unlimited"}'
    WHEN 'Classic jobs and pipelines'     THEN 'Use a job-only compute policy with the same mandatory custom_tags.CostCenter and custom_tags.Owner rules; add job tags'
    WHEN 'Serverless jobs and pipelines'  THEN 'Assign a serverless usage policy with CostCenter and Owner tags to the jobs and pipelines'
    WHEN 'Serverless notebooks'           THEN 'Assign a serverless usage policy with CostCenter and Owner tags to the users or groups'
    WHEN 'SQL warehouses'                 THEN 'Add CostCenter and Owner tags on each SQL warehouse (Edit > Advanced options > Tags)'
    WHEN 'Model serving'                  THEN 'Add CostCenter and Owner tags on each serving endpoint or a serverless usage policy'
    ELSE 'Tag the resource where the product supports tags, or use a serverless usage policy'
  END                                                                    AS recommended_fix,
  ROUND(b.unattributed_usd, 2)                                           AS affected_cost_usd,
  CAST(0 AS DOUBLE)                                                      AS estimated_waste_usd,
  CAST(0 AS DOUBLE)                                                      AS estimated_monthly_saving_usd,
  'Visibility item: no direct saving; enables chargeback and owner follow-up'
                                                                         AS saving_basis,
  ROUND(b.cost_usd, 2)                                                   AS category_cost_usd,
  ROUND(b.no_cost_center_usd, 2)                                         AS no_cost_center_usd,
  ROUND(b.no_owner_usd, 2)                                               AS no_owner_usd
FROM by_category AS b
LEFT JOIN system.access.workspaces_latest AS w ON w.workspace_id = b.workspace_id
WHERE b.unattributed_usd > 0
ORDER BY affected_cost_usd DESC;
