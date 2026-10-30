# v1.1 edge cases → automated tests (feature pack section B.8)

Every edge case the feature pack lists is one named, automated test (xUnit unless marked). Files are under `tests/PmoReview.Tests/` (`Unit/`, `Contract/`), `src/web/` (SPA unit and Playwright). Graph and ARM are mocked in every test (R9).

## F1 — Resource* selector (`Unit/F1ResourceSelectorTests.cs`)
| Edge case | Test |
|---|---|
| empty sheet | `F1_empty_sheet_offers_no_options_and_resolve_is_refused` |
| tenant with no PH/ACQ | `F1_tenant_with_no_PH_or_ACQ_hides_those_options` |
| same group via two domains | `F1_same_group_via_two_domains_is_listed_once_with_both_sources` |
| same group two levels | `F1_same_group_at_two_levels_shows_the_highest_and_notes_the_lower_as_included` |
| PowerBI misspelling variants | `F1_PowerBI_misspelling_variants_are_not_merged_silently`, `F1_near_duplicates_canonical_is_the_one_entra_resolves_after_a_worker_run` |
| group name that doesn't resolve | `F1_group_name_that_does_not_resolve_is_unresolved_escalated_and_never_requested`, `F1_a_group_still_to_be_created_is_unresolved` |
| user from tenant A asking for tenant B | `F1_user_from_tenant_A_asking_for_tenant_B_gets_not_found`, `F1_a_row_whose_I1_tenant_is_another_tenant_is_never_offered` |
| "All" then untick one | `F1_All_then_untick_one_resolves_the_rest_only`; SPA `src/resources.test.ts` "All then untick one clears All…"; Playwright "Resource* checkboxes: All ticks every resource, unticking one clears All…" |
| dropdown vs checkbox give same resolved list | `F1_dropdown_and_checkbox_give_the_same_resolved_list` |
| Unicode/whitespace in names | `F1_unicode_and_whitespace_in_names_are_normalised` |
| 1,000+ rows performance | `F1_1000_plus_rows_resolve_within_two_seconds` |
| (added) contract shape, Prod statuses, retired/expired rows, bundle | `F1_resolve_returns_exactly_the_output_contract`, `F1_prod_above_read_is_needs_human_approval_and_prod_read_is_auto`, `F1_retired_deprecated_red_and_expired_rows_are_not_offered`, `F1_bundle_*` |

## F2 — TAP mail to the enterprise mail only (`Unit/F2TapRecipientTests.cs`)
| Edge case | Test |
|---|---|
| EnterpriseEmail empty | `F2_EnterpriseEmail_empty_sends_nothing_and_escalates` |
| guest #EXT# UPN | `F2_Guest_EXT_upn_is_never_a_recipient` |
| inventory vs Entra mismatch | `F2_Inventory_vs_Entra_mismatch_sends_nothing_and_escalates` (and the matching case `F2_Entra_mail_that_matches_the_inventory_is_the_one_recipient`) |
| AlternateEmail present | `F2_AlternateEmail_present_is_never_used` |
| body tries to override recipient | `F2_Body_tries_to_override_recipient` |
| mail send fails | `F2_Mail_send_fails_leaves_the_pass_to_the_id_team_and_audits_without_the_pass` |
| TAP never appears in logs | `F2_TAP_never_appears_in_logs` |
| (added) not in inventory / leaver, personal domain / manager, one recipient / no Cc-Bcc / no redirect, escalation visibility | `F2_Not_in_the_inventory_or_a_leaver_is_never_mailed`, `F2_Personal_domains_managers_and_display_names_are_refused`, `F2_One_recipient_no_cc_or_bcc_and_no_redirect`, `F2_The_escalation_is_listed_for_the_id_team_and_pmo_without_the_address`; `Unit/AgentTapTests.cs` `In_a_test_environment_the_tap_mail_is_never_redirected` |

## F3 — Prod read-only + human escalation (`Unit/F3ProdGuardTests.cs`, `Unit/F3UploadPathTests.cs`)
| Edge case | Test |
|---|---|
| Prod write via UI | `F3_prod_write_via_ui_bundle_is_needs_human_approval_and_escalated` |
| via API with forged body | `F3_prod_write_via_api_with_a_forged_body_is_still_escalated`, `F3_prod_write_via_the_api_waits_after_both_approvals_and_nothing_is_granted` |
| via upload | `F3_Prod_write_via_upload_is_never_granted_without_an_approved_escalation` |
| via external API | `F3_Prod_write_via_external_api_is_never_granted_without_an_approved_escalation` |
| approver = requester | `F3_approver_who_is_the_requester_is_refused`, `F3_approver_who_resubmitted_the_request_is_refused`, `F3_approver_who_gets_the_access_is_refused` (and `F3_an_earlier_approver_of_the_same_request_cannot_decide_the_prod_stage`) |
| approval after the request was changed (stale revision) | `F3_approval_after_the_request_was_changed_is_stale`, `F3_an_approved_escalation_for_an_older_revision_never_lets_the_agent_grant` |
| no code path grants Prod write without an Approved escalation | `F3_no_code_path_grants_prod_write_without_an_approved_escalation` (forged state at both granting stages), plus the four path tests above |
| (added) | `F3_an_environment_less_writer_row_requested_for_dev_is_still_escalated`, `F3_a_row_labelled_read_that_carries_a_writing_role_is_still_escalated`, `F3_prod_read_only_is_fulfilled_without_an_escalation`, `F3_a_rejected_escalation_closes_the_request_and_grants_nothing`, `F3_prod_approver_approval_sends_it_through_the_normal_audited_path`, `F3_escalations_are_listed_only_for_roles_that_handle_them`, `F3_guard_rules_cover_levels_roles_and_privilege` |

## F4 — Uploads + external API (`Unit/F4UploadTests.cs`)
| Edge case | Test |
|---|---|
| missing/renamed column | `F4_Missing_or_renamed_column_stops_the_file` |
| wrong tenant rows | `F4_Wrong_tenant_rows_block_the_whole_file` |
| duplicate keys | `F4_Duplicate_rows_and_duplicate_keys_are_refused` |
| macro file | `F4_Macro_and_binary_files_are_refused` |
| empty file | `F4_Empty_file_header_only_and_blank_lines_are_refused` |
| huge file | `F4_Huge_files_too_many_rows_and_zip_bombs_are_refused` |
| re-upload of identical file (no new changes) | `F4_Reupload_of_an_identical_file_makes_no_new_version` |
| retire-missing off vs on | `F4_Retire_missing_rows_off_leaves_them_on_retires_them` |
| concurrent uploads (ETag) | `F4_Concurrent_uploads_are_refused_by_the_version_and_the_head_etag` |
| rollback | `F4_Rollback_restores_the_previous_version_in_one_click` |
| external API without app role | `F4_External_api_without_the_app_role_or_a_valid_token_is_refused` |
| replayed Idempotency-Key | `F4_Replayed_idempotency_key_returns_the_first_answer_and_never_applies_twice` (keys per calling app) |
| (added) | `F4_External_api_returns_exactly_the_contract_and_uses_the_same_code_as_the_page`, `F4_Only_the_DataMaintainer_uploads_and_only_in_their_own_projects`, `F4_Xlsx_first_sheet_or_named_sheet_with_date_cells_and_numbers`, `F4_Csv_with_bom_quotes_semicolons_and_formula_like_values_is_read_as_data`, `F4_An_upload_keeps_the_azure_roles_and_flags_of_existing_rows`, `F4_Near_duplicate_group_names_in_one_environment_are_refused_unless_both_have_object_ids`, `F4_People_inventory_upload_validates_enterprise_ids_and_feeds_the_tap_recipient`; SPA `src/uploads.test.ts`; Playwright "Data uploads: the DataMaintainer checks a mapping file…" |

## F5 — Offboarding (`Unit/F5OffboardingTests.cs`, `Contract/OffboardingAdapterTests.cs`)
| Edge case | Test |
|---|---|
| user not found | `F5_User_not_found_stops_with_nothing_changed` |
| two users same display name | `F5_Two_users_with_the_same_display_name_only_the_named_enterprise_id_is_offboarded` |
| already disabled | `F5_Already_disabled_account_is_not_patched_again` |
| partial failure midway then re-run (idempotent) | `F5_Partial_failure_midway_then_rerun_changes_nothing_twice`, `F5_Something_removed_by_hand_after_the_plan_is_read_first_and_not_removed_again` |
| no RBAC rights on one subscription | `F5_No_RBAC_rights_on_one_subscription_is_a_leftover_and_the_rest_is_done` |
| requester = confirmer in Prod | `F5_Requester_equals_confirmer_in_a_production_tenant_is_refused` (a tenant never marked counts as production) |
| PMO contact missing | `F5_PMO_contact_missing_completes_without_a_mail_and_says_so` |
| verify finds leftovers → no completion mail | `F5_Verify_finds_leftovers_so_no_completion_mail`, `F5_A_directory_role_holder_is_left_to_people_and_nothing_is_changed_in_Entra_or_Azure`, `F5_A_request_opened_after_the_plan_is_a_leftover_and_a_leaver_gets_no_new_request` |
| double-click button | `F5_Double_click_on_plan_and_confirm_runs_everything_once`; SPA `src/offboarding.test.ts`; Playwright "Offboard user: PMO asks for a plan…" |
| (added) | `F5_Only_the_projects_own_people_are_offboarded`, `F5_Plan_confirm_execute_verify_audit_in_order_and_only_the_pmo_is_told`, `F5_Execute_needs_the_switch_a_ready_and_fresh_plan_and_the_right_role`, `F5_Guest_and_soft_delete_when_switched_on`; adapter contracts: exact Graph/ARM URLs, `/$ref` only, users-only RBAC delete, `nextLink` paging (`The_arm_reader_follows_next_links_and_reports_a_list_it_cannot_finish_as_unreadable`) |

## P6 independent review findings → tests (`REVIEW.md`)
| Finding | Test(s) |
|---|---|
| 1 Critical: offboarding not bound to the project; directory-role holders (re-check: High remainder, a request alone counted) | `F5_Only_the_projects_own_people_are_offboarded` (approved requests only), `F5_A_person_another_project_of_the_tenant_still_holds_is_not_offboarded_from_one_project` (the re-check probe), `F5_A_directory_role_holder_is_left_to_people_and_nothing_is_changed_in_Entra_or_Azure`; Playwright "Offboard user: PMO asks for a plan…" (not found before the approvals) |
| 2 High: four eyes never effective | `F5_Requester_equals_confirmer_in_a_production_tenant_is_refused` (unset = production); `DataModelV11Tests` (every SAMPLE tenant states it); `tools/test_import_workbook.py` (`--production-tenant`) |
| 3 High: ProdApprover who resubmitted or gets the access | `F3_approver_who_resubmitted_the_request_is_refused`, `F3_approver_who_gets_the_access_is_refused` |
| 4 High: environment-less row bypass | `F3_an_environment_less_writer_row_requested_for_dev_is_still_escalated` |
| 5 High: retired / expired rows | `Unit/RetiredRowTests.cs` (5 tests: retired, expired or not yet effective, retired after the review, ended after the review, rename with the old row retired) |
| 6 High: expired plan blocks for good | `F5_Execute_needs_the_switch_a_ready_and_fresh_plan_and_the_right_role` (re-plan after expiry, then execute) |
| 9 Medium (part): re-provisioning after offboarding | `F5_A_request_opened_after_the_plan_is_a_leftover_and_a_leaver_gets_no_new_request` |
| 10 Medium: ARM `nextLink` | `The_arm_reader_follows_next_links_and_reports_a_list_it_cannot_finish_as_unreadable` |
| 13 Low: keys fetched for any tenant | `F4_External_api_without_the_app_role_or_a_valid_token_is_refused` (no key lookup for an unknown tenant) |
| 14 Low: Prod spellings | `DataModelV11Tests.Access_levels_rank_read_lowest_and_environments_know_uat` (Prod-EU, Prod2, prd-weu, Production, LIVE are Prod; PreProd, ppd are not) |
| 16 Low: idempotency per app | `F4_Replayed_idempotency_key_returns_the_first_answer_and_never_applies_twice` |
| 20 Medium (new, re-check): a half-executed run closed as "nothing was changed" | `F5_A_confirmed_run_is_finished_and_verified_even_if_the_person_is_retired_meanwhile` |
| 21 Low (new, re-check): directory role given after the plan | `F5_A_directory_role_given_after_the_plan_stops_every_entra_and_azure_write` |
| 9 (re-check remainder): a person known only from requests requests again after offboarding | `F5_Only_the_projects_own_people_are_offboarded` (`409 person_is_leaver`) |
| 17 Low: `Cancelled` status | removed (enum, SPA types); SPA `src/offboarding.test.ts` "offers a new plan when the confirm says the shown plan expired" |
