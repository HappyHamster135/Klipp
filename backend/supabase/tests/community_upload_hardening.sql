-- Rollback-only regression checks after community_upload_hardening. Creates only
-- reserved QA accounts/application rows; never writes Storage-managed metadata.
begin;
set local lock_timeout = '2s';
set local statement_timeout = '20s';
create function pg_temp.harden_assert(p_value boolean, p_message text) returns void language plpgsql as $$
begin if p_value is distinct from true then raise exception 'FAIL: %', p_message; end if; end $$;
create function pg_temp.harden_expect(p_sql text, p_state text) returns void language plpgsql as $$
begin
    begin execute p_sql; exception when others then
        if sqlstate = p_state then return; end if;
        raise exception 'Expected %, got %: %', p_state, sqlstate, sqlerrm;
    end;
    raise exception 'Expected %, query succeeded: %', p_state, p_sql;
end $$;

select pg_temp.harden_assert(not exists(select 1 from pg_proc p join pg_namespace n on n.oid=p.pronamespace
    where n.nspname='public' and p.proname like 'community_%' and p.prosecdef), 'public RPCs remain invoker');
select pg_temp.harden_assert(not has_table_privilege('authenticated', 'klipp_private.community_upload_events', 'INSERT')
    and not has_table_privilege('anon', 'klipp_private.community_upload_expirations', 'SELECT'), 'limits and cleanup markers private');
select pg_temp.harden_assert(not has_function_privilege('anon', 'klipp_private.community_home_maintenance_allowed()', 'EXECUTE'), 'proof helper not exposed');
select pg_temp.harden_assert(not has_function_privilege('anon', 'public.community_begin_home_upload(text,text,double precision,bigint)', 'EXECUTE'), 'anonymous reservation denied');
select pg_temp.harden_assert((select count(*)=1 from pg_policies where schemaname='storage' and tablename='objects'
    and policyname='community_storage_no_new_cloud_uploads' and permissive='RESTRICTIVE' and cmd='INSERT'
    and roles @> array['anon','authenticated']::name[]), 'cloud insert denied independently of permissive policies');
select pg_temp.harden_assert(not exists(select 1 from pg_policies where schemaname='storage' and policyname='community_storage_upload'), 'old permissive upload policy removed');

insert into auth.users(id, email_confirmed_at)
    select ('e96a4ee1-a1c0-4abc-90ef-' || lpad(i::text,12,'0'))::uuid, now() from generate_series(1,14) i;
update public.profiles set username='harden_test_' || right(id::text,3)
    where id::text like 'e96a4ee1-a1c0-4abc-90ef-%';
update auth.users set email_confirmed_at=null where id='e96a4ee1-a1c0-4abc-90ef-000000000003';
update klipp_private.community_home_config set uploads_enabled=true,
    gateway_secret_sha256=encode(extensions.digest(repeat('hardening-test-',6),'sha256'),'hex') where singleton;
insert into public.community_clips(id,owner_id,title,duration_seconds,byte_size,storage_provider,status,upload_expires_at) values
    ('e96a4ee1-cccc-4abc-90ef-000000000001','e96a4ee1-a1c0-4abc-90ef-000000000001','Old cloud clip',1,10,'supabase','published',now()-interval '1 hour'),
    ('e96a4ee1-cccc-4abc-90ef-000000000002','e96a4ee1-a1c0-4abc-90ef-000000000001','Published home clip',1,10,'home','published',now()-interval '1 hour'),
    ('e96a4ee1-cccc-4abc-90ef-000000000003','e96a4ee1-a1c0-4abc-90ef-000000000001','Manual deletion',1,10,'home','deleting',now()-interval '1 hour'),
    ('e96a4ee1-cccc-4abc-90ef-000000000004','e96a4ee1-a1c0-4abc-90ef-000000000001','Expired pending',1,10,'home','uploading',now()-interval '1 minute'),
    ('e96a4ee1-cccc-4abc-90ef-000000000005','e96a4ee1-a1c0-4abc-90ef-000000000001','Current pending',1,10,'home','uploading',now()+interval '20 minutes');

set local role authenticated;
select set_config('request.headers','{}',true);
select set_config('request.jwt.claim.sub','e96a4ee1-a1c0-4abc-90ef-000000000001',true);
select pg_temp.harden_expect('select public.community_begin_upload(''Old client'', '''', 1, 10)', 'KM018');
select pg_temp.harden_expect('select public.community_publish_clip(''e96a4ee1-cccc-4abc-90ef-000000000001'')', 'KM018');
select pg_temp.harden_expect('select klipp_private.community_dispatch(''begin'', ''{}'')', 'KM018');
select pg_temp.harden_expect('select klipp_private.community_dispatch(''publish'', ''{}'')', 'KM018');
select pg_temp.harden_assert(not klipp_private.community_storage_access('community-videos',
    'e96a4ee1-a1c0-4abc-90ef-000000000001/e96a4ee1-cccc-4abc-90ef-000000000001/video.mp4','insert'), 'custom native client cannot insert cloud files');
select pg_temp.harden_assert(klipp_private.community_storage_access('community-videos',
    'e96a4ee1-a1c0-4abc-90ef-000000000001/e96a4ee1-cccc-4abc-90ef-000000000001/video.mp4','select'), 'legacy playback preserved');
select public.community_remove_clip('e96a4ee1-cccc-4abc-90ef-000000000001');
select pg_temp.harden_assert(klipp_private.community_storage_access('community-videos',
    'e96a4ee1-a1c0-4abc-90ef-000000000001/e96a4ee1-cccc-4abc-90ef-000000000001/video.mp4','delete'), 'legacy owner cleanup preserved');
select set_config('request.jwt.claim.sub','e96a4ee1-a1c0-4abc-90ef-000000000002',true);
select pg_temp.harden_assert(not klipp_private.community_storage_access('community-videos',
    'e96a4ee1-a1c0-4abc-90ef-000000000001/e96a4ee1-cccc-4abc-90ef-000000000001/video.mp4','delete'), 'other member cannot remove legacy files');
select set_config('request.jwt.claim.sub','e96a4ee1-a1c0-4abc-90ef-000000000003',true);
select pg_temp.harden_expect('select public.community_begin_home_upload(''Unverified'', '''', 1, 10)', 'KM001');
select set_config('request.jwt.claim.sub','e96a4ee1-a1c0-4abc-90ef-000000000002',true);
select set_config('request.headers','{"origin":"null"}',true);
select pg_temp.harden_expect('select public.community_begin_home_upload(''Phone'', '''', 1, 10)', 'KM014');
select set_config('request.headers','{"sec-fetch-mode":"cors"}',true);
select pg_temp.harden_expect('select public.community_begin_home_upload(''Browser'', '''', 1, 10)', 'KM014');
select set_config('request.headers','{}',true);
select set_config('test.harden_reservation', public.community_begin_home_upload('Native upload', '', 1, 10)->>'id', true);
select pg_temp.harden_assert((public.community_get_clip(current_setting('test.harden_reservation')::uuid)->>'upload_expires_at')::timestamptz
    between now()+interval '29 minutes' and clock_timestamp()+interval '31 minutes', 'native reservation gets server-owned expiry');
select public.community_begin_home_upload('Native upload two', '', 1, 10);
select pg_temp.harden_expect('select public.community_begin_home_upload(''Third pending'', '''', 1, 10)', 'KM019');
select set_config('request.jwt.claim.sub','e96a4ee1-a1c0-4abc-90ef-000000000001',true);
select set_config('request.headers',jsonb_build_object('x-klipp-media-secret',repeat('hardening-test-',6))::text,true);
select pg_temp.harden_expect('select public.community_home_publish(''e96a4ee1-cccc-4abc-90ef-000000000004'',10,10,1,repeat(''a'',64),repeat(''b'',64))','KM017');
reset role;

set local role anon;
select set_config('request.headers','{}',true);
select pg_temp.harden_expect('select public.community_home_claim_expired_uploads()', 'KM015');
select pg_temp.harden_expect('select public.community_home_finish_expired_upload(''e96a4ee1-cccc-4abc-90ef-000000000004'')', 'KM015');
select set_config('request.headers',jsonb_build_object('x-klipp-media-secret',repeat('wrong-',12))::text,true);
select pg_temp.harden_expect('select public.community_home_claim_expired_uploads()', 'KM015');
select set_config('request.headers',jsonb_build_object('x-klipp-media-secret',repeat('hardening-test-',6),'origin','null')::text,true);
select pg_temp.harden_expect('select public.community_home_claim_expired_uploads()', 'KM015');
select set_config('request.headers',jsonb_build_object('x-klipp-media-secret',repeat('hardening-test-',6))::text,true);
select pg_temp.harden_expect('select public.community_home_claim_expired_uploads(11)', 'KM003');
select pg_temp.harden_expect('select public.community_home_claim_expired_uploads(null)', 'KM003');
select pg_temp.harden_expect('select public.community_home_finish_expired_upload(''e96a4ee1-cccc-4abc-90ef-000000000003'')','KM010');
select pg_temp.harden_assert(public.community_home_claim_expired_uploads() = jsonb_build_array(
    jsonb_build_object('id','e96a4ee1-cccc-4abc-90ef-000000000004','owner_id','e96a4ee1-a1c0-4abc-90ef-000000000001'),
    jsonb_build_object('id','e96a4ee1-cccc-4abc-90ef-000000000003','owner_id','e96a4ee1-a1c0-4abc-90ef-000000000001')),
    'janitor claims expired pending and owner-authorized home deletion only');
select pg_temp.harden_assert(jsonb_array_length(public.community_home_claim_expired_uploads())=2, 'interrupted cleanup retries both tombstones');
select pg_temp.harden_expect('select public.community_home_finish_expired_upload(''e96a4ee1-cccc-4abc-90ef-000000000001'')','KM010');
select pg_temp.harden_expect('select public.community_home_finish_expired_upload(''e96a4ee1-cccc-4abc-90ef-000000000002'')','KM010');
select pg_temp.harden_expect('select public.community_home_finish_expired_upload(''e96a4ee1-cccc-4abc-90ef-000000000005'')','KM010');
reset role;
select pg_temp.harden_assert((select count(*)=5 from public.community_clips where owner_id='e96a4ee1-a1c0-4abc-90ef-000000000001'), 'claim retains quota until physical cleanup');
select pg_temp.harden_assert((select count(*)=2 from klipp_private.community_upload_expirations), 'expired upload and requested deletion marked');
set local role authenticated;
select set_config('request.jwt.claim.sub','e96a4ee1-a1c0-4abc-90ef-000000000001',true);
select pg_temp.harden_expect('select public.community_begin_home_upload(''Still full'', '''', 1, 10)', 'KM004');
reset role;
set local role anon;
select pg_temp.harden_assert((public.community_home_finish_expired_upload('e96a4ee1-cccc-4abc-90ef-000000000004')->>'deleted')::boolean, 'gateway cleanup releases slot');
select pg_temp.harden_assert((public.community_home_finish_expired_upload('e96a4ee1-cccc-4abc-90ef-000000000004')->>'deleted')::boolean, 'cleanup finish is idempotent');
select pg_temp.harden_assert((public.community_home_finish_expired_upload('e96a4ee1-cccc-4abc-90ef-000000000003')->>'deleted')::boolean, 'gateway can complete claimed owner-authorized home deletion');
select pg_temp.harden_assert(public.community_home_claim_expired_uploads()='[]'::jsonb, 'cleaned reservation no longer returned');
reset role;
select pg_temp.harden_assert(not exists(select 1 from klipp_private.community_upload_expirations), 'cleanup marker cascades');
select pg_temp.harden_assert((select status='uploading' from public.community_clips where id='e96a4ee1-cccc-4abc-90ef-000000000005'), 'current upload preserved');

-- A valid publish wins before expiry; old creation time does not expire an
-- already-published clip and an idempotent retry stays available.
set local role authenticated;
select set_config('request.jwt.claim.sub','e96a4ee1-a1c0-4abc-90ef-000000000002',true);
select set_config('request.headers',jsonb_build_object('x-klipp-media-secret',repeat('hardening-test-',6))::text,true);
select pg_temp.harden_assert(public.community_home_publish(current_setting('test.harden_reservation')::uuid,10,10,1,repeat('a',64),repeat('b',64))->>'status'='published','current upload can publish');
reset role;
update public.community_clips set upload_expires_at=now()-interval '1 hour' where id=current_setting('test.harden_reservation')::uuid;
set local role authenticated;
select pg_temp.harden_assert(public.community_home_publish(current_setting('test.harden_reservation')::uuid,10,10,1,repeat('a',64),repeat('b',64))->>'status'='published','published retry ignores expired reservation timestamp');
reset role;

-- Rolling rate counters are independent of existing clips and deletion.
insert into klipp_private.community_upload_events(owner_id,created_at)
    select 'e96a4ee1-a1c0-4abc-90ef-000000000004',now() from generate_series(1,5);
set local role authenticated;
select set_config('request.jwt.claim.sub','e96a4ee1-a1c0-4abc-90ef-000000000004',true);
select set_config('request.headers','{}',true);
select pg_temp.harden_expect('select public.community_begin_home_upload(''Hourly limited'', '''', 1, 10)','KM008');
reset role;
update klipp_private.community_upload_events set created_at=now()-interval '2 hours' where owner_id='e96a4ee1-a1c0-4abc-90ef-000000000004';
insert into klipp_private.community_upload_events(owner_id,created_at)
    select 'e96a4ee1-a1c0-4abc-90ef-000000000004',now()-interval '2 hours' from generate_series(1,15);
set local role authenticated;
select pg_temp.harden_expect('select public.community_begin_home_upload(''Daily limited'', '''', 1, 10)','KM008');
reset role;
update klipp_private.community_upload_events set created_at=now()-interval '2 days' where owner_id='e96a4ee1-a1c0-4abc-90ef-000000000004';
set local role authenticated;
select public.community_begin_home_upload('Old events aged out', '', 1, 10);
reset role;
select pg_temp.harden_assert((select count(*)=1 from klipp_private.community_upload_events where owner_id='e96a4ee1-a1c0-4abc-90ef-000000000004'),'old ledger entries pruned');

-- Fill pending slots with root-owned QA fixtures, then prove the global bound.
insert into public.community_clips(owner_id,title,duration_seconds,byte_size,storage_provider)
    select 'e96a4ee1-a1c0-4abc-90ef-000000000005','Pending bound',1,10,'home'
    from generate_series(1,greatest(0,10-(select count(*)::integer from public.community_clips where storage_provider='home' and status='uploading')));
set local role authenticated;
select set_config('request.jwt.claim.sub','e96a4ee1-a1c0-4abc-90ef-000000000006',true);
select pg_temp.harden_expect('select public.community_begin_home_upload(''Global pending full'', '''', 1, 10)','KM019');
reset role;

delete from public.community_clips where owner_id='e96a4ee1-a1c0-4abc-90ef-000000000005';
insert into klipp_private.community_upload_events(owner_id,created_at)
    select 'e96a4ee1-a1c0-4abc-90ef-000000000014',now() from generate_series(1,100);
set local role authenticated;
select pg_temp.harden_expect('select public.community_begin_home_upload(''Global hourly limit'', '''', 1, 10)','KM008');
reset role;
update klipp_private.community_upload_events set created_at=now()-interval '2 hours'
    where owner_id::text like 'e96a4ee1-a1c0-4abc-90ef-%';
insert into klipp_private.community_upload_events(owner_id,created_at)
    select 'e96a4ee1-a1c0-4abc-90ef-000000000014',now()-interval '2 hours' from generate_series(1,200);
set local role authenticated;
select pg_temp.harden_expect('select public.community_begin_home_upload(''Global daily limit'', '''', 1, 10)','KM008');
reset role;

-- More than a batch of stale uploads is processed fairly. Retrying an unreadable
-- directory does not permanently starve the eleventh record.
insert into public.community_clips(id,owner_id,title,duration_seconds,byte_size,storage_provider,upload_expires_at)
    select ('e96a4ee1-dddd-4abc-90ef-' || lpad(i::text,12,'0'))::uuid,
        'e96a4ee1-a1c0-4abc-90ef-000000000005','Expired batch',1,10,'home',now()-interval '2 hours'
    from generate_series(1,11) i;
select set_config('request.headers',jsonb_build_object('x-klipp-media-secret',repeat('hardening-test-',6))::text,true);
set local role anon;
select pg_temp.harden_assert(jsonb_array_length(public.community_home_claim_expired_uploads())=10, 'janitor batch capped at ten');
select pg_temp.harden_assert(public.community_home_claim_expired_uploads()->0->>'id'='e96a4ee1-dddd-4abc-90ef-000000000011', 'later expired records get a turn before retries');
reset role;
select pg_temp.harden_assert((select count(*)=11 from klipp_private.community_upload_expirations), 'all eleven expirations retain metadata pending file cleanup');

rollback;
select 'upload hardening checks passed; fixtures rolled back' as result;
