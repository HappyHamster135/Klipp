-- Read-only aggregate receipt; no user IDs, message text or credentials.
select jsonb_build_object(
    'operations_enabled',(select operations_enabled from klipp_private.community_operations_config where singleton),
    'account_deletion_enabled',(select account_deletion_enabled from klipp_private.community_operations_config where singleton),
    'enabled_operators',(select count(*)from klipp_private.community_operators where enabled),
    'open_reports',(select count(*)from public.community_reports where status='open'),
    'open_support',(select count(*)from klipp_private.community_support where status='open'),
    'pending_account_deletions',(select count(*)from klipp_private.community_account_deletions),
    'hidden_clips',(select count(*)from public.community_clips where moderation_hidden),
    'unknown_provider_metrics',(select count(*)from klipp_private.community_provider_usage where used is null),
    'public_definer_rpcs',(select count(*)from pg_proc p join pg_namespace n on n.oid=p.pronamespace where n.nspname='public'and p.proname like'community_%'and p.prosecdef),
    'operator_allowlist_private',not has_table_privilege('authenticated','klipp_private.community_operators','SELECT')
        and not has_table_privilege('authenticated','klipp_private.community_operators','INSERT'),
    'support_private',not has_table_privilege('authenticated','klipp_private.community_support','SELECT'),
    'final_readiness_service_only',not has_function_privilege('authenticated','public.community_account_delete_ready(uuid)','EXECUTE')
        and has_function_privilege('service_role','public.community_account_delete_ready(uuid)','EXECUTE')
)as operations_deployment_check;
