-- Read-only deployment receipt. Never emits credentials, email, paths or IDs.
select jsonb_build_object(
    'native_upload_expiry_column', exists(select 1 from information_schema.columns
        where table_schema='public' and table_name='community_clips' and column_name='upload_expires_at' and is_nullable='NO'),
    'cloud_insert_closed', exists(select 1 from pg_policies where schemaname='storage' and tablename='objects'
        and policyname='community_storage_no_new_cloud_uploads' and permissive='RESTRICTIVE' and cmd='INSERT'
        and roles @> array['anon','authenticated']::name[])
        and not exists(select 1 from pg_policies where schemaname='storage' and policyname='community_storage_upload'),
    'public_definer_rpcs', (select count(*) from pg_proc p join pg_namespace n on n.oid=p.pronamespace
        where n.nspname='public' and p.proname like 'community_%' and p.prosecdef),
    'pending_home_uploads', (select count(*) from public.community_clips where storage_provider='home' and status='uploading'),
    'expired_pending_home_uploads', (select count(*) from public.community_clips where storage_provider='home'
        and status='uploading' and upload_expires_at<=clock_timestamp()),
    'expiry_cleanup_pending', (select count(*) from klipp_private.community_upload_expirations),
    'home_deletions_awaiting_claim', (select count(*) from public.community_clips c where c.storage_provider='home' and c.status='deleting'
        and not exists(select 1 from klipp_private.community_upload_expirations e where e.clip_id=c.id)),
    'home_pending_work', (select count(*) from public.community_clips where storage_provider='home' and status in ('uploading','deleting')),
    'recent_reservations', (select count(*) from klipp_private.community_upload_events where created_at>=now()-interval '1 day'),
    'legacy_clips_preserved', (select count(*) from public.community_clips where storage_provider='supabase'),
    'limits_table_private', not has_table_privilege('authenticated','klipp_private.community_upload_events','INSERT')
        and not has_table_privilege('anon','klipp_private.community_upload_expirations','SELECT'),
    'proof_helper_private', not has_function_privilege('anon','klipp_private.community_home_maintenance_allowed()','EXECUTE')
        and not has_function_privilege('authenticated','klipp_private.community_home_maintenance_allowed()','EXECUTE')
) as upload_hardening_check;
