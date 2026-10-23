-- Rollback-only regression: owner removal must not strand upload capacity.
-- No Storage-managed metadata or files are written by these database checks.
begin;
set local lock_timeout='2s';
set local statement_timeout='20s';
create function pg_temp.queue_assert(p_value boolean,p_message text) returns void language plpgsql as $$
begin if p_value is distinct from true then raise exception 'FAIL: %',p_message; end if; end $$;
create function pg_temp.queue_expect(p_sql text,p_state text) returns void language plpgsql as $$
begin
    begin execute p_sql; exception when others then
        if sqlstate=p_state then return; end if;
        raise exception 'Expected %, got %: %',p_state,sqlstate,sqlerrm;
    end;
    raise exception 'Expected %, query succeeded: %',p_state,p_sql;
end $$;

insert into auth.users(id,email_confirmed_at) values
    ('e97a4ee1-a1c0-4abc-90ef-000000000001',now()),
    ('e97a4ee1-a1c0-4abc-90ef-000000000002',now());
update public.profiles set username='queue_test_' || right(id::text,3)
    where id::text like 'e97a4ee1-a1c0-4abc-90ef-%';
update klipp_private.community_home_config set uploads_enabled=true,
    gateway_secret_sha256=encode(extensions.digest(repeat('queue-test-only-',6),'sha256'),'hex') where singleton;
select set_config('request.headers','{}',true);
set local role authenticated;
select set_config('request.jwt.claim.sub','e97a4ee1-a1c0-4abc-90ef-000000000001',true);
select set_config('test.queue_first',public.community_begin_home_upload('First pending','',1,10)->>'id',true);
select set_config('test.queue_second',public.community_begin_home_upload('Second pending','',1,10)->>'id',true);
select public.community_remove_clip(current_setting('test.queue_first')::uuid);
select public.community_remove_clip(current_setting('test.queue_second')::uuid);
select pg_temp.queue_expect('select public.community_begin_home_upload(''Reserve-remove bypass'','''',1,10)','KM019');
select pg_temp.queue_expect('update public.community_clips set status=''published'' where id=current_setting(''test.queue_first'')::uuid','42501');
select set_config('request.jwt.claim.sub','e97a4ee1-a1c0-4abc-90ef-000000000002',true);
select pg_temp.queue_expect('select public.community_remove_clip(current_setting(''test.queue_first'')::uuid)','KM011');
reset role;
select pg_temp.queue_assert((select count(*)=2 from public.community_clips where owner_id='e97a4ee1-a1c0-4abc-90ef-000000000001' and status='deleting'),'reserve-remove retains both physical cleanup slots');
select pg_temp.queue_assert(not exists(select 1 from klipp_private.community_upload_expirations where clip_id in
    (current_setting('test.queue_first')::uuid,current_setting('test.queue_second')::uuid)),'owner removal starts unclaimed');

insert into public.community_clips(id,owner_id,title,duration_seconds,byte_size,storage_provider,status,upload_expires_at) values
    ('e97a4ee1-cccc-4abc-90ef-000000000001','e97a4ee1-a1c0-4abc-90ef-000000000001','Published home retained',1,10,'home','published',now()-interval '1 hour'),
    ('e97a4ee1-cccc-4abc-90ef-000000000002','e97a4ee1-a1c0-4abc-90ef-000000000001','Owner removes published home',1,10,'home','published',now()+interval '25 minutes'),
    ('e97a4ee1-cccc-4abc-90ef-000000000003','e97a4ee1-a1c0-4abc-90ef-000000000001','Legacy removal preserved',1,10,'supabase','published',now()+interval '25 minutes'),
    ('e97a4ee1-cccc-4abc-90ef-000000000004','e97a4ee1-a1c0-4abc-90ef-000000000002','Current upload retained',1,10,'home','uploading',now()+interval '25 minutes'),
    ('e97a4ee1-cccc-4abc-90ef-000000000005','e97a4ee1-a1c0-4abc-90ef-000000000002','Expired pending cleanup',1,10,'home','uploading',now()-interval '1 minute');
set local role authenticated;
select set_config('request.jwt.claim.sub','e97a4ee1-a1c0-4abc-90ef-000000000001',true);
select public.community_remove_clip('e97a4ee1-cccc-4abc-90ef-000000000002');
select public.community_remove_clip('e97a4ee1-cccc-4abc-90ef-000000000003');
select pg_temp.queue_assert(klipp_private.community_storage_access('community-videos',
    'e97a4ee1-a1c0-4abc-90ef-000000000001/e97a4ee1-cccc-4abc-90ef-000000000003/video.mp4','delete'),'legacy owner file deletion remains allowed');
reset role;

set local role anon;
select pg_temp.queue_expect('select public.community_home_claim_expired_uploads()','KM015');
select pg_temp.queue_expect('select public.community_home_finish_expired_upload(current_setting(''test.queue_first'')::uuid)','KM015');
select set_config('request.headers',jsonb_build_object('x-klipp-media-secret',repeat('wrong-proof-',6))::text,true);
select pg_temp.queue_expect('select public.community_home_claim_expired_uploads()','KM015');
select set_config('request.headers',jsonb_build_object('x-klipp-media-secret',repeat('queue-test-only-',6))::text,true);
select pg_temp.queue_expect('select public.community_home_finish_expired_upload(current_setting(''test.queue_first'')::uuid)','KM010');
select pg_temp.queue_expect('select public.community_home_finish_expired_upload(''e97a4ee1-cccc-4abc-90ef-000000000002'')','KM010');
select set_config('test.queue_claims',public.community_home_claim_expired_uploads()::text,true);
select pg_temp.queue_assert(jsonb_array_length(current_setting('test.queue_claims')::jsonb)=4,'expired pending and all authorized home tombstones join cleanup');
select pg_temp.queue_assert(current_setting('test.queue_claims')::jsonb @> jsonb_build_array(jsonb_build_object('id',current_setting('test.queue_first')))
    and current_setting('test.queue_claims')::jsonb @> jsonb_build_array(jsonb_build_object('id',current_setting('test.queue_second')))
    and current_setting('test.queue_claims')::jsonb @> '[{"id":"e97a4ee1-cccc-4abc-90ef-000000000002"},{"id":"e97a4ee1-cccc-4abc-90ef-000000000005"}]'::jsonb,
    'claim set contains both manually removed pending, explicitly removed published and expired home clips');
select pg_temp.queue_assert(jsonb_array_length(public.community_home_claim_expired_uploads())=4,'interrupted manual and expiry cleanup retries idempotently');
select pg_temp.queue_expect('select public.community_home_finish_expired_upload(''e97a4ee1-cccc-4abc-90ef-000000000001'')','KM010');
select pg_temp.queue_expect('select public.community_home_finish_expired_upload(''e97a4ee1-cccc-4abc-90ef-000000000003'')','KM010');
select pg_temp.queue_expect('select public.community_home_finish_expired_upload(''e97a4ee1-cccc-4abc-90ef-000000000004'')','KM010');
reset role;
select pg_temp.queue_assert((select count(*)=7 from public.community_clips where owner_id in
    ('e97a4ee1-a1c0-4abc-90ef-000000000001','e97a4ee1-a1c0-4abc-90ef-000000000002')),'claiming cleanup does not release any quota');
select pg_temp.queue_assert((select count(*)=4 from klipp_private.community_upload_expirations where clip_id in
    (current_setting('test.queue_first')::uuid,current_setting('test.queue_second')::uuid,'e97a4ee1-cccc-4abc-90ef-000000000002','e97a4ee1-cccc-4abc-90ef-000000000005')),'all selected home removals get private claim markers');
select pg_temp.queue_assert((select status='published' from public.community_clips where id='e97a4ee1-cccc-4abc-90ef-000000000001'),'published home clip untouched');
select pg_temp.queue_assert((select status='uploading' from public.community_clips where id='e97a4ee1-cccc-4abc-90ef-000000000004'),'unexpired pending clip untouched');
select pg_temp.queue_assert((select status='deleting' from public.community_clips where id='e97a4ee1-cccc-4abc-90ef-000000000003'),'legacy removal remains for Storage API cleanup');

-- The gateway calls these acknowledgements only after physical deletion. The
-- database release is idempotent and only accepts the previously claimed set.
set local role anon;
select pg_temp.queue_assert((public.community_home_finish_expired_upload(current_setting('test.queue_first')::uuid)->>'deleted')::boolean,'claimed pending owner removal can finish');
select pg_temp.queue_assert((public.community_home_finish_expired_upload(current_setting('test.queue_first')::uuid)->>'deleted')::boolean,'manual cleanup acknowledgement retry is idempotent');
select pg_temp.queue_assert((public.community_home_finish_expired_upload(current_setting('test.queue_second')::uuid)->>'deleted')::boolean,'second pending owner removal can finish');
select pg_temp.queue_assert((public.community_home_finish_expired_upload('e97a4ee1-cccc-4abc-90ef-000000000002')->>'deleted')::boolean,'explicit owner removal of published home clip can finish');
select pg_temp.queue_assert((public.community_home_finish_expired_upload('e97a4ee1-cccc-4abc-90ef-000000000005')->>'deleted')::boolean,'expired pending cleanup still finishes');
select pg_temp.queue_assert(public.community_home_claim_expired_uploads()='[]'::jsonb,'completed claims disappear and protected clips stay excluded');
reset role;
select pg_temp.queue_assert(not exists(select 1 from klipp_private.community_upload_expirations where clip_id in
    (current_setting('test.queue_first')::uuid,current_setting('test.queue_second')::uuid,'e97a4ee1-cccc-4abc-90ef-000000000002','e97a4ee1-cccc-4abc-90ef-000000000005')),'claim markers cascade only after confirmed removal');
set local role authenticated;
select set_config('request.jwt.claim.sub','e97a4ee1-a1c0-4abc-90ef-000000000001',true);
select set_config('request.headers','{}',true);
select pg_temp.queue_assert(public.community_begin_home_upload('Capacity recovered','',1,10)->>'status'='uploading','new upload works after physical cleanup acknowledgement');
select pg_temp.queue_assert((public.community_finish_delete('e97a4ee1-cccc-4abc-90ef-000000000003')->>'deleted')::boolean,'legacy final deletion path remains usable');
reset role;

rollback;
select 'home cleanup queue checks passed; fixtures rolled back' as result;
