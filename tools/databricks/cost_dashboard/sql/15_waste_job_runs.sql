-- =============================================================================
-- Dataset 15  waste_job_runs           Page 4 Waste               Visual: table (+ counter)
-- Waste item 6: failed or repaired job runs, and long-running job runs, per job.
--
-- Run cost = billing.usage joined on usage_metadata.job_id / job_run_id (Learn:
-- "Calculate cost per job run"). Covers job compute and serverless jobs; jobs
-- on all-purpose compute have no job_run_id in billing (see item 4).
-- Run facts from job_run_timeline (runs over an hour have several rows;
-- result_state is set only on the row that ends the run; a repaired run has
-- more than one row with a result_state - the Learn "retried job runs" pattern).
--   6a Failed or repaired runs:
--      final state FAILED / ERROR / TIMED_OUT -> waste = whole run cost
--      repaired, then SUCCEEDED               -> waste = cost up to the last failure
--   6b Long-running runs (successful, not repaired):
--      run minutes > 2 x the job's median AND at least 30 minutes over it,
--      job with at least 3 such runs          -> waste = run cost - median run cost
-- Monthly saving = period waste x 30 / days in period.
--
-- Tables  system.lakeflow.job_run_timeline, system.lakeflow.jobs (regional),
--         system.billing.usage, system.billing.list_prices,
--         system.access.workspaces_latest (global).
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
run_hour_cost AS (
  SELECT workspace_id, usage_metadata.job_id AS job_id, usage_metadata.job_run_id AS run_id,
         date_trunc('HOUR', usage_start_time) AS usage_hour,
         SUM(list_cost_usd) AS cost_usd,
         max_by(identity_metadata.run_as, usage_end_time) AS run_as
  FROM priced
  WHERE billing_origin_product = 'JOBS'
    AND usage_metadata.job_id IS NOT NULL AND usage_metadata.job_run_id IS NOT NULL
  GROUP BY workspace_id, usage_metadata.job_id, usage_metadata.job_run_id, date_trunc('HOUR', usage_start_time)
),
runs AS (
  SELECT
    workspace_id, job_id, run_id,
    SUM(timestampdiff(SECOND, period_start_time, period_end_time)) / 60.0                AS run_minutes,
    COUNT(result_state)                                                                   AS terminal_rows,
    max_by(result_state, CASE WHEN result_state IS NOT NULL THEN period_end_time END)     AS final_state,
    max_by(termination_code, CASE WHEN result_state IS NOT NULL THEN period_end_time END) AS final_termination_code,
    MAX(CASE WHEN result_state IN ('FAILED', 'ERROR', 'TIMED_OUT') THEN period_end_time END) AS last_failure_end,
    max_by(termination_code,
           CASE WHEN result_state IN ('FAILED', 'ERROR', 'TIMED_OUT') THEN period_end_time END) AS failure_termination_code
  FROM system.lakeflow.job_run_timeline
  WHERE period_start_time >= date_sub(:start_date, 1) AND period_start_time < date_add(:end_date, 1)
  GROUP BY workspace_id, job_id, run_id
),
run_level AS (
  SELECT
    r.*,
    c.cost_usd,
    c.cost_until_last_failure_usd,
    c.run_as,
    CASE WHEN r.final_state IN ('FAILED', 'ERROR', 'TIMED_OUT')       THEN 'failed'
         WHEN r.final_state = 'SUCCEEDED' AND r.last_failure_end IS NOT NULL THEN 'repaired'
         WHEN r.final_state = 'SUCCEEDED'                              THEN 'succeeded'
         ELSE 'other' END                                               AS outcome
  FROM runs AS r
  JOIN (
    SELECT h.workspace_id, h.job_id, h.run_id,
           SUM(h.cost_usd) AS cost_usd,
           SUM(CASE WHEN h.usage_hour <= date_trunc('HOUR', r2.last_failure_end) THEN h.cost_usd ELSE 0 END)
                                                                        AS cost_until_last_failure_usd,
           max_by(h.run_as, h.usage_hour) AS run_as
    FROM run_hour_cost AS h
    JOIN runs AS r2 ON r2.workspace_id = h.workspace_id AND r2.job_id = h.job_id AND r2.run_id = h.run_id
    GROUP BY h.workspace_id, h.job_id, h.run_id
  ) AS c
    ON c.workspace_id = r.workspace_id AND c.job_id = r.job_id AND c.run_id = r.run_id
),
job_baseline AS (
  SELECT workspace_id, job_id,
         percentile_approx(run_minutes, 0.5) AS median_minutes,
         percentile_approx(cost_usd, 0.5)    AS median_cost_usd,
         COUNT(*)                            AS clean_runs
  FROM run_level
  WHERE outcome = 'succeeded'
  GROUP BY workspace_id, job_id
),
issues AS (
  SELECT
    workspace_id, job_id,
    '6a Failed or repaired job runs'                                    AS waste_item,
    COUNT(*)                                                            AS affected_runs,
    SUM(cost_usd)                                                       AS affected_cost_usd,
    SUM(CASE WHEN outcome = 'failed' THEN cost_usd ELSE cost_until_last_failure_usd END) AS waste_usd,
    concat(CAST(SUM(CASE WHEN outcome = 'failed' THEN 1 ELSE 0 END) AS STRING), ' failed, ',
           CAST(SUM(CASE WHEN outcome = 'repaired' THEN 1 ELSE 0 END) AS STRING), ' repaired; codes: ',
           COALESCE(array_join(slice(array_sort(collect_set(failure_termination_code)), 1, 3), ', '), 'n/a'))
                                                                        AS finding,
    'Fix the root cause shown by the termination codes; set task retries with a retry interval, a job timeout and a failure notification (job > Duration and timeouts / Notifications)'
                                                                        AS recommended_fix,
    'Failed run: whole run cost; repaired run: cost up to the last failure'
                                                                        AS saving_basis,
    max_by(run_as, cost_usd)                                            AS run_as
  FROM run_level
  WHERE outcome IN ('failed', 'repaired')
  GROUP BY workspace_id, job_id
  UNION ALL
  SELECT
    rl.workspace_id, rl.job_id,
    '6b Long-running job runs',
    COUNT(*),
    SUM(rl.cost_usd),
    SUM(GREATEST(rl.cost_usd - b.median_cost_usd, 0)),
    concat(CAST(COUNT(*) AS STRING), ' runs over 2x the median of ',
           CAST(ROUND(MAX(b.median_minutes), 0) AS STRING), ' min; longest ',
           CAST(ROUND(MAX(rl.run_minutes), 0) AS STRING), ' min'),
    'Set a job timeout and a duration warning threshold; check data skew, input growth and cluster size',
    'Run cost above the job''s median run cost',
    max_by(rl.run_as, rl.cost_usd)
  FROM run_level AS rl
  JOIN job_baseline AS b ON b.workspace_id = rl.workspace_id AND b.job_id = rl.job_id
  WHERE rl.outcome = 'succeeded'
    AND b.clean_runs >= 3                                      -- EDIT HERE: minimum runs for a baseline
    AND rl.run_minutes > 2 * b.median_minutes                  -- EDIT HERE: x median
    AND rl.run_minutes - b.median_minutes >= 30                -- EDIT HERE: minutes over median
  GROUP BY rl.workspace_id, rl.job_id
),
jobs_latest AS (
  SELECT workspace_id, job_id, name, run_as_user_name, creator_user_name
  FROM (SELECT j.*, ROW_NUMBER() OVER (PARTITION BY j.workspace_id, j.job_id ORDER BY j.change_time DESC) AS rn
        FROM system.lakeflow.jobs AS j) AS t
  WHERE rn = 1
)
SELECT
  i.waste_item,
  i.workspace_id,
  COALESCE(w.workspace_name, concat('workspace ', i.workspace_id))       AS workspace_name,
  'Job'                                                                  AS resource_type,
  i.job_id                                                               AS resource_id,
  COALESCE(jl.name, i.job_id)                                            AS resource_name,
  COALESCE(jl.run_as_user_name, i.run_as, jl.creator_user_name)          AS owner,
  i.finding,
  i.recommended_fix,
  ROUND(i.affected_cost_usd, 2)                                          AS affected_cost_usd,
  ROUND(i.waste_usd, 2)                                                  AS estimated_waste_usd,
  ROUND(i.waste_usd * 30.0 / (datediff(:end_date, :start_date) + 1), 2)  AS estimated_monthly_saving_usd,
  i.saving_basis,
  i.affected_runs
FROM issues AS i
LEFT JOIN jobs_latest AS jl ON jl.workspace_id = i.workspace_id AND jl.job_id = i.job_id
LEFT JOIN system.access.workspaces_latest AS w ON w.workspace_id = i.workspace_id
WHERE i.waste_usd > 0
ORDER BY estimated_monthly_saving_usd DESC;
