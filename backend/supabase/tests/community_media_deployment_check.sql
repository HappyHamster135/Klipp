-- Read-only production deployment check: schema/policy metadata and aggregate
-- capacity only. Does not reveal accounts, emails, object paths, or media content.
select jsonb_build_object(
    'tables_with_rls', (select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace
        where n.nspname = 'public' and c.relname in ('community_clips', 'community_likes',
            'community_comments', 'community_blocks', 'community_reports') and c.relrowsecurity),
    'public_invoker_rpcs', (select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace
        where n.nspname = 'public' and p.proname like 'community_%' and not p.prosecdef
            and p.proconfig @> array['search_path=""']::text[]),
    'anonymous_rpc_access', (select count(*) from pg_proc p join pg_namespace n on n.oid = p.pronamespace
        where n.nspname = 'public' and p.proname like 'community_%'
            and has_function_privilege('anon', p.oid, 'execute')),
    'client_direct_table_access', (select count(*) from pg_class c join pg_namespace n on n.oid = c.relnamespace
        where n.nspname = 'public' and c.relname in ('community_clips', 'community_likes',
            'community_comments', 'community_blocks', 'community_reports')
            and (has_table_privilege('authenticated', c.oid, 'select,insert,update,delete')
                or has_table_privilege('anon', c.oid, 'select,insert,update,delete'))),
    'storage_policies', (select count(*) from pg_policies where schemaname = 'storage'
        and tablename = 'objects' and policyname like 'community_storage_%'),
    'rpc_only_deny_policies', (select count(*) from pg_policies where
        schemaname in ('public', 'klipp_private') and policyname like 'community_%_rpc_only'
        and permissive = 'RESTRICTIVE' and cmd = 'ALL' and roles @> array['anon', 'authenticated']::name[]
        and qual = 'false' and with_check = 'false'),
    'correct_private_buckets', (select count(*) from storage.buckets b where not b.public and (
        (b.id = 'community-videos' and b.file_size_limit = 16777216 and b.allowed_mime_types = array['video/mp4']::text[])
        or (b.id = 'community-posters' and b.file_size_limit = 262144 and b.allowed_mime_types = array['image/jpeg']::text[]))),
    'reserved_slots', (select count(*) from public.community_clips),
    'reserved_capacity_bytes', (select count(*) * 17039360 from public.community_clips)
) as deployment_check;
-- Expected: tables_with_rls=5, public_invoker_rpcs=13,
-- anonymous_rpc_access=0, client_direct_table_access=0,
-- storage_policies=8, rpc_only_deny_policies=6, correct_private_buckets=2,
-- reserved_slots<=40. Apply community_rpc_only_policies before expecting 6.
