-- Read-only checks. No account details, file paths, gateway digest or secrets.
select jsonb_build_object(
    'home_config_private', not has_table_privilege('authenticated', 'klipp_private.community_home_config', 'SELECT')
        and not has_table_privilege('anon', 'klipp_private.community_home_config', 'SELECT'),
    'home_config_rls', (select relrowsecurity from pg_class where oid='klipp_private.community_home_config'::regclass),
    'home_uploads_enabled', (select uploads_enabled from klipp_private.community_home_config where singleton),
    'gateway_configured', (select gateway_secret_sha256 is not null from klipp_private.community_home_config where singleton),
    'anonymous_home_rpc_access', (select count(*) from pg_proc p join pg_namespace n on n.oid=p.pronamespace
        where n.nspname='public' and p.proname in ('community_begin_home_upload','community_home_publish','community_home_finish_delete')
        and has_function_privilege('anon',p.oid,'EXECUTE')),
    'public_definer_rpcs', (select count(*) from pg_proc p join pg_namespace n on n.oid=p.pronamespace
        where n.nspname='public' and p.proname like 'community_%' and p.prosecdef),
    'reserved_slots', (select count(*) from public.community_clips),
    'legacy_clips', (select count(*) from public.community_clips where storage_provider='supabase'),
    'home_clips', (select count(*) from public.community_clips where storage_provider='home'),
    'home_published_without_evidence', (select count(*) from public.community_clips where storage_provider='home'
        and status='published' and (video_sha256 is null or poster_sha256 is null or poster_byte_size is null))
) as home_deployment_check;
