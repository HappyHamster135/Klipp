-- Transaction-only integration checks. All fixture accounts, reservations and
-- configuration changes roll back. Never modifies Storage-managed tables.
begin;
create function pg_temp.home_assert(p_value boolean, p_message text) returns void language plpgsql as $$
begin if p_value is distinct from true then raise exception 'FAIL: %', p_message; end if; end $$;
create function pg_temp.home_expect(p_sql text, p_state text) returns void language plpgsql as $$
begin
    begin execute p_sql; exception when others then
        if sqlstate = p_state then return; end if;
        raise exception 'Expected %, got %: %', p_state, sqlstate, sqlerrm;
    end;
    raise exception 'Expected %, query succeeded: %', p_state, p_sql;
end $$;

select pg_temp.home_assert(not has_table_privilege('authenticated', 'klipp_private.community_home_config', 'SELECT'), 'gateway digest private');
select pg_temp.home_assert(not has_function_privilege('authenticated', 'klipp_private.community_home_gateway_allowed()', 'EXECUTE'), 'proof helper not callable');
select pg_temp.home_assert(not exists(select 1 from pg_proc p join pg_namespace n on n.oid=p.pronamespace
    where n.nspname='public' and p.proname like 'community_%' and p.prosecdef), 'all public RPCs invoker');
select pg_temp.home_assert(not has_function_privilege('anon', 'public.community_begin_home_upload(text,text,double precision,bigint)', 'EXECUTE'), 'anonymous reservation denied');

-- These fixed fixture IDs are reserved to this rollback-only test.
insert into auth.users(id, email_confirmed_at) values
    ('e91827d8-0069-4abc-90ef-000000000001', now()),
    ('e91827d8-0069-4abc-90ef-000000000002', now()),
    ('e91827d8-0069-4abc-90ef-000000000003', null);
update public.profiles set username='klipptest_' || right(id::text, 3)
    where id in ('e91827d8-0069-4abc-90ef-000000000001','e91827d8-0069-4abc-90ef-000000000002','e91827d8-0069-4abc-90ef-000000000003');
select set_config('request.headers', '{}', true);
set local role authenticated;
select set_config('request.jwt.claim.sub', 'e91827d8-0069-4abc-90ef-000000000003', true);
select pg_temp.home_expect('select public.community_begin_home_upload(''Unverified'', '''', 1, 10)', 'KM001');
select set_config('request.jwt.claim.sub', 'e91827d8-0069-4abc-90ef-000000000001', true);
select pg_temp.home_expect('select public.community_begin_home_upload(''Disabled'', '''', 1, 10)', 'KM016');
select pg_temp.home_expect('select public.community_begin_home_upload(''Invalid'', '''', ''NaN'', 10)', 'KM003');
select set_config('request.headers', '{"origin":"https://happyhamster135.github.io"}', true);
select pg_temp.home_expect('select public.community_begin_home_upload(''Browser'', '''', 1, 10)', 'KM014');
select set_config('request.headers', '{}', true);
reset role;
-- This is a public, test-only value. Production uses a fresh random capability.
update klipp_private.community_home_config set uploads_enabled=true,
    gateway_secret_sha256=encode(extensions.digest(repeat('test-only-',8),'sha256'),'hex') where singleton;
set local role authenticated;
select set_config('test.home_clip', public.community_begin_home_upload('Home fixture', '', 1, 10)->>'id', true);
select pg_temp.home_assert(public.community_get_clip(current_setting('test.home_clip')::uuid)->>'storage_provider'='home', 'provider survives DTO');
select pg_temp.home_assert(not klipp_private.community_storage_access('community-videos',
    'e91827d8-0069-4abc-90ef-000000000001/' || current_setting('test.home_clip') || '/video.mp4', 'insert'), 'home cannot use Supabase Storage');
select pg_temp.home_expect('select public.community_publish_clip(current_setting(''test.home_clip'')::uuid)', 'KM015');
select pg_temp.home_expect('select public.community_home_publish(current_setting(''test.home_clip'')::uuid,10,10,1,repeat(''a'',64),repeat(''b'',64))', 'KM015');
select set_config('request.headers', jsonb_build_object('x-klipp-media-secret',repeat('wrong-',16))::text, true);
select pg_temp.home_expect('select public.community_home_publish(current_setting(''test.home_clip'')::uuid,10,10,1,repeat(''a'',64),repeat(''b'',64))', 'KM015');
select set_config('request.headers', jsonb_build_object('x-klipp-media-secret',repeat('test-only-',8))::text, true);
select set_config('request.jwt.claim.sub', 'e91827d8-0069-4abc-90ef-000000000002', true);
select pg_temp.home_expect('select public.community_home_publish(current_setting(''test.home_clip'')::uuid,10,10,1,repeat(''a'',64),repeat(''b'',64))', 'KM007');
select set_config('request.jwt.claim.sub', 'e91827d8-0069-4abc-90ef-000000000001', true);
select pg_temp.home_expect('select public.community_home_publish(current_setting(''test.home_clip'')::uuid,11,10,1,repeat(''a'',64),repeat(''b'',64))', 'KM006');
select pg_temp.home_assert(public.community_home_publish(current_setting('test.home_clip')::uuid,10,10,1,repeat('a',64),repeat('b',64))->>'status'='published', 'gateway publishes owner clip');
select pg_temp.home_assert(public.community_home_publish(current_setting('test.home_clip')::uuid,10,10,1,repeat('a',64),repeat('b',64))->>'status'='published', 'publish retry idempotent');
select pg_temp.home_expect('select public.community_home_publish(current_setting(''test.home_clip'')::uuid,10,10,1,repeat(''c'',64),repeat(''b'',64))', 'KM010');
select set_config('request.headers', '{}', true);
select pg_temp.home_expect('select public.community_finish_delete(current_setting(''test.home_clip'')::uuid)', 'KM010');
select public.community_remove_clip(current_setting('test.home_clip')::uuid);
select pg_temp.home_expect('select public.community_finish_delete(current_setting(''test.home_clip'')::uuid)', 'KM015');
select pg_temp.home_expect('select public.community_home_finish_delete(current_setting(''test.home_clip'')::uuid)', 'KM015');
select set_config('request.headers', jsonb_build_object('x-klipp-media-secret',repeat('test-only-',8))::text, true);
select set_config('request.jwt.claim.sub', 'e91827d8-0069-4abc-90ef-000000000002', true);
select pg_temp.home_expect('select public.community_home_finish_delete(current_setting(''test.home_clip'')::uuid)', 'KM011');
select set_config('request.jwt.claim.sub', 'e91827d8-0069-4abc-90ef-000000000001', true);
select pg_temp.home_assert((public.community_home_finish_delete(current_setting('test.home_clip')::uuid)->>'deleted')::boolean, 'attested cleanup releases slot');
select set_config('request.headers', '{}', true);
-- Legacy and home rows share the same five-slot per-account budget.
select public.community_begin_upload('Legacy quota fixture', '', 1, 10);
select public.community_begin_home_upload('Home quota fixture 1', '', 1, 10);
select public.community_begin_home_upload('Home quota fixture 2', '', 1, 10);
select public.community_begin_home_upload('Home quota fixture 3', '', 1, 10);
select public.community_begin_home_upload('Home quota fixture 4', '', 1, 10);
select pg_temp.home_expect('select public.community_begin_home_upload(''Over quota'', '''', 1, 10)', 'KM004');
select pg_temp.home_expect('select public.community_begin_upload(''Over quota'', '''', 1, 10)', 'KM004');
reset role;
-- Fill the remaining shared global slots inside this rollback transaction only.
insert into public.community_clips(owner_id,title,duration_seconds,byte_size,storage_provider)
    select 'e91827d8-0069-4abc-90ef-000000000001', 'Global quota fixture', 1, 10, 'home'
    from generate_series(1, greatest(0, 40 - (select count(*)::integer from public.community_clips)));
set local role authenticated;
select set_config('request.jwt.claim.sub', 'e91827d8-0069-4abc-90ef-000000000002', true);
select pg_temp.home_expect('select public.community_begin_home_upload(''Global full'', '''', 1, 10)', 'KM005');
select pg_temp.home_expect('select public.community_begin_upload(''Global full'', '''', 1, 10)', 'KM005');
reset role;
rollback;
select 'home media checks passed; fixtures rolled back' as result;
