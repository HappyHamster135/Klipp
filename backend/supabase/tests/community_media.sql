-- Local PostgreSQL integration test. Storage rows below stand in for Storage API
-- metadata; this does not claim to decode media or exercise a hosted blob service.
-- Fixture-only writes must NEVER be run against the production Storage schema.
begin;
create function pg_temp.assert_true(p_value boolean, p_message text) returns void language plpgsql as $$
begin if p_value is distinct from true then raise exception 'FAIL: %', p_message; end if; end $$;
create function pg_temp.expect_sqlstate(p_sql text, p_state text) returns void language plpgsql as $$
begin
    begin execute p_sql; exception when others then
        if sqlstate = p_state then return; end if;
        raise exception 'Expected %, got %: %', p_state, sqlstate, sqlerrm;
    end;
    raise exception 'Expected error %, query succeeded: %', p_state, p_sql;
end $$;
create function pg_temp.expect_affected(p_sql text, p_expected bigint) returns void language plpgsql as $$
declare v_count bigint;
begin execute p_sql; get diagnostics v_count = row_count;
    if v_count <> p_expected then raise exception 'Expected % affected rows, got %', p_expected, v_count; end if;
end $$;
insert into storage.buckets(id, public, file_size_limit, allowed_mime_types) values
    ('community-videos', false, 16777216, array['video/mp4']),
    ('community-posters', false, 262144, array['image/jpeg']);
insert into auth.users(id, email_confirmed_at)
    select ('10000000-0000-0000-0000-' || lpad(i::text, 12, '0'))::uuid, now() from generate_series(1, 12) i;
update public.profiles set username = 'member_' || right(id::text, 2);
update auth.users set is_anonymous = true where id = '10000000-0000-0000-0000-000000000010';
update auth.users set email_confirmed_at = null where id = '10000000-0000-0000-0000-000000000011';
update public.profiles set username = null where id = '10000000-0000-0000-0000-000000000012';

select pg_temp.assert_true(not exists(select 1 from pg_proc p join pg_namespace n on n.oid = p.pronamespace
    where n.nspname = 'public' and p.proname like 'community_%' and p.prosecdef), 'exposed RPCs remain invoker');
select pg_temp.assert_true(not has_table_privilege('authenticated', 'public.community_clips', 'INSERT')
    and not has_table_privilege('authenticated', 'public.community_reports', 'SELECT'), 'no quota bypass or report reads');
select pg_temp.assert_true(not has_function_privilege('anon', 'public.community_feed(integer,timestamp with time zone,uuid,boolean)', 'EXECUTE')
    and not has_function_privilege('authenticated', 'klipp_private.community_clip_json(uuid,uuid)', 'EXECUTE'), 'helper grants restricted');
select pg_temp.assert_true((select bool_and(relrowsecurity) from pg_class where oid in
    ('public.community_clips'::regclass, 'public.community_likes'::regclass, 'public.community_comments'::regclass,
     'public.community_blocks'::regclass, 'public.community_reports'::regclass)), 'all exposed tables RLS');
select pg_temp.assert_true((select count(*) = 6 from pg_policies where
    (schemaname = 'public' or schemaname = 'klipp_private') and policyname like 'community_%_rpc_only'
    and permissive = 'RESTRICTIVE' and cmd = 'ALL' and roles @> array['anon', 'authenticated']::name[]
    and qual = 'false' and with_check = 'false'), 'explicit restrictive client deny policies');

set local role anon;
select pg_temp.expect_sqlstate('select public.community_feed()', '42501');
select pg_temp.assert_true((select count(*) = 0 from storage.objects), 'anonymous Storage reads denied');
reset role;
set local role authenticated;
select set_config('request.jwt.claim.sub', '', true);
select pg_temp.expect_sqlstate('select public.community_feed()', 'KM001');
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000010', true);
select pg_temp.expect_sqlstate('select public.community_begin_upload(''Bad'', '''', 10, 1024)', 'KM001');
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000011', true);
select pg_temp.expect_sqlstate('select public.community_feed()', 'KM001');
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000012', true);
select pg_temp.expect_sqlstate('select public.community_feed()', 'KM002');
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000001', true);
select pg_temp.expect_sqlstate('select public.community_begin_upload('''', '''', 10, 1024)', 'KM003');
select pg_temp.expect_sqlstate('select public.community_begin_upload(''Bad'', '''', 61, 1024)', 'KM003');
select pg_temp.expect_sqlstate('select public.community_begin_upload(''Bad'', '''', ''NaN'', 1024)', 'KM003');
select pg_temp.expect_sqlstate('select public.community_begin_upload(''Bad'', '''', 10, 16777217)', 'KM003');
select pg_temp.expect_sqlstate('select public.community_feed(0)', 'KM003');
select pg_temp.expect_sqlstate('select public.community_feed(13, now(), null, false)', 'KM003');
select set_config('test.clip', public.community_begin_upload('First', 'A small test', 10, 1024)->>'id', true);
select pg_temp.assert_true((public.community_get_clip(current_setting('test.clip')::uuid)->>'status') = 'uploading', 'owner sees own pending upload');
select pg_temp.assert_true(jsonb_array_length(public.community_feed()) = 0 and jsonb_array_length(public.community_feed(p_mine => true)) = 1, 'pending only in My Uploads');
select pg_temp.expect_sqlstate('update public.community_clips set owner_id = gen_random_uuid()', '42501');
select pg_temp.expect_sqlstate('select public.community_publish_clip(current_setting(''test.clip'')::uuid)', 'KM006');
select pg_temp.expect_sqlstate($q$insert into storage.objects(bucket_id,name,owner_id,metadata)
    values('community-videos','10000000-0000-0000-0000-000000000001/bad/video.mp4',auth.uid()::text,'{}')$q$, '42501');
insert into storage.objects(bucket_id,name,owner_id,metadata) values('community-videos',
    '10000000-0000-0000-0000-000000000001/' || current_setting('test.clip') || '/video.mp4', auth.uid()::text,
    '{"size":2048,"mimetype":"video/mp4"}');
insert into storage.objects(bucket_id,name,owner_id,metadata) values('community-posters',
    '10000000-0000-0000-0000-000000000001/' || current_setting('test.clip') || '/poster.jpg', auth.uid()::text,
    '{"size":200,"mimetype":"image/jpeg"}');
select pg_temp.expect_sqlstate('select public.community_publish_clip(current_setting(''test.clip'')::uuid)', 'KM006');
-- Replacement is denied even if another broad permissive policy exists.
reset role;
create policy fixture_broad_policy on storage.objects for all to authenticated, anon using(true) with check(true);
set local role authenticated;
select pg_temp.expect_affected($q$update storage.objects set metadata = '{"size":1024}'$q$, 0);
reset role;
update storage.objects set metadata = '{"size":1024,"mimetype":"video/mp4"}' where bucket_id = 'community-videos';
set local role authenticated;
select pg_temp.assert_true(public.community_publish_clip(current_setting('test.clip')::uuid)->>'status' = 'published', 'metadata checked before publish');
select pg_temp.assert_true((select count(*) = 2 from storage.objects), 'owner reads files');
select pg_temp.expect_affected('delete from storage.objects', 0);
reset role;
-- The explicit deny remains effective if future code accidentally adds a table
-- grant and a permissive policy. Checked privileged RPCs retain their behavior.
grant select on public.community_clips to authenticated;
create policy fixture_metadata_permissive on public.community_clips for select to authenticated using(true);
set local role authenticated;
select pg_temp.assert_true((select count(*) = 0 from public.community_clips)
    and jsonb_array_length(public.community_feed()) = 1, 'restrictive deny blocks accidental grants without breaking RPC');
reset role;
drop policy fixture_metadata_permissive on public.community_clips;
revoke select on public.community_clips from authenticated;
set local role anon;
select pg_temp.assert_true((select count(*) = 0 from storage.objects), 'anonymous reads denied even with broad Storage policy');
reset role;
-- Same-time clip ordering uses the UUID tie-breaker and does not repeat items.
insert into public.community_clips(id, owner_id, title, duration_seconds, byte_size, status, created_at) values
    ('20000000-0000-0000-0000-000000000001', '10000000-0000-0000-0000-000000000003', 'Page one', 1, 1, 'published', '2050-01-01'),
    ('20000000-0000-0000-0000-000000000002', '10000000-0000-0000-0000-000000000003', 'Page two', 1, 1, 'published', '2050-01-01');
set local role authenticated;
select pg_temp.assert_true(public.community_feed(1)->0->>'id' = '20000000-0000-0000-0000-000000000002'
    and public.community_feed(1, '2050-01-01', '20000000-0000-0000-0000-000000000002')->0->>'id'
        = '20000000-0000-0000-0000-000000000001', 'feed stable keyset tie-breaker');
reset role;
delete from public.community_clips where id in ('20000000-0000-0000-0000-000000000001', '20000000-0000-0000-0000-000000000002');
set local role authenticated;

select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000002', true);
select pg_temp.assert_true(jsonb_array_length(public.community_feed()) = 1, 'other verified member sees feed');
select pg_temp.expect_sqlstate('select public.community_remove_clip(current_setting(''test.clip'')::uuid)', 'KM011');
select pg_temp.expect_sqlstate($q$insert into storage.objects(bucket_id,name,owner_id,metadata)
    values('community-videos','10000000-0000-0000-0000-000000000001/' || current_setting('test.clip') || '/other.mp4',auth.uid()::text,'{}')$q$, '42501');
select pg_temp.assert_true((public.community_set_like(current_setting('test.clip')::uuid, true)->>'like_count')::int = 1
    and (public.community_set_like(current_setting('test.clip')::uuid, true)->>'liked_by_me')::boolean, 'idempotent like');
select set_config('test.comment', public.community_add_comment(current_setting('test.clip')::uuid, 'Hello')->>'id', true);
select pg_temp.assert_true(jsonb_array_length(public.community_comments(current_setting('test.clip')::uuid)) = 1, 'comment returns page');
select pg_temp.assert_true(public.community_report_clip(current_setting('test.clip')::uuid, 'Test report')->>'reported' = 'true', 'report accepted privately');
select pg_temp.expect_sqlstate('select * from public.community_reports', '42501');
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000001', true);
select pg_temp.expect_sqlstate('select public.community_delete_comment(current_setting(''test.comment'')::uuid)', 'KM011');
select pg_temp.assert_true(public.community_block_user('10000000-0000-0000-0000-000000000002', true)->>'blocked' = 'true', 'block accepted');
select pg_temp.assert_true(jsonb_array_length(public.community_blocked_members()) = 1
    and public.community_blocked_members()->0->>'user_id' = '10000000-0000-0000-0000-000000000002', 'only own blocked members listed');
select pg_temp.assert_true(jsonb_array_length(public.community_comments(current_setting('test.clip')::uuid)) = 0, 'blocked authors hidden in comments');
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000002', true);
select pg_temp.assert_true(jsonb_array_length(public.community_feed()) = 0 and (select count(*) = 0 from storage.objects), 'bidirectional block hides feed and Storage');
select pg_temp.expect_sqlstate('select public.community_get_clip(current_setting(''test.clip'')::uuid)', 'KM007');
select pg_temp.expect_sqlstate('select public.community_set_like(current_setting(''test.clip'')::uuid,true)', 'KM007');
select pg_temp.assert_true(jsonb_array_length(public.community_blocked_members()) = 0, 'reverse block does not expose who blocked the member');
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000001', true);
reset role;
update public.profiles set username = null where id = '10000000-0000-0000-0000-000000000001';
set local role authenticated;
select pg_temp.assert_true(jsonb_array_length(public.community_blocked_members()) = 1, 'blocked list survives cleared own handle');
select public.community_block_user('10000000-0000-0000-0000-000000000002', false);
reset role;
update public.profiles set username = 'member_01' where id = '10000000-0000-0000-0000-000000000001';
set local role authenticated;
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000002', true);
select public.community_delete_comment(current_setting('test.comment')::uuid);
select public.community_add_comment(current_setting('test.clip')::uuid, 'Two');
select public.community_add_comment(current_setting('test.clip')::uuid, 'Three');
select public.community_add_comment(current_setting('test.clip')::uuid, 'Four');
select public.community_add_comment(current_setting('test.clip')::uuid, 'Five');
select pg_temp.expect_sqlstate('select public.community_add_comment(current_setting(''test.clip'')::uuid, ''Six'')', 'KM008');
-- Tuple cursors remain stable when several records have the same timestamp.
reset role;
delete from klipp_private.community_comment_events;
insert into public.community_comments(clip_id, owner_id, body, created_at)
    select current_setting('test.clip')::uuid, '10000000-0000-0000-0000-000000000003', 'Page ' || i, '2020-01-01'::timestamptz
    from generate_series(1, 96) i;
set local role authenticated;
select pg_temp.expect_sqlstate('select public.community_add_comment(current_setting(''test.clip'')::uuid, ''Cap'')', 'KM009');
select pg_temp.assert_true(jsonb_array_length(public.community_comments(current_setting('test.clip')::uuid, 31)) = 31, 'bounded comment page');
select pg_temp.assert_true(jsonb_array_length(public.community_comments(current_setting('test.clip')::uuid, 50,
    (public.community_comments(current_setting('test.clip')::uuid, 31)->30->>'created_at')::timestamptz,
    (public.community_comments(current_setting('test.clip')::uuid, 31)->30->>'id')::uuid)) = 50, 'older comments use tuple keyset');
-- An incomplete author cannot poison another member's public feed/comments.
reset role;
update public.profiles set username = null where id = '10000000-0000-0000-0000-000000000003';
set local role authenticated;
select pg_temp.assert_true(jsonb_array_length(public.community_comments(current_setting('test.clip')::uuid)) = 4, 'incomplete authors hidden');

select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000001', true);
select pg_temp.assert_true(public.community_remove_clip(current_setting('test.clip')::uuid)->>'status' = 'deleting', 'remove marks reservation deleting');
select pg_temp.expect_sqlstate('select public.community_finish_delete(current_setting(''test.clip'')::uuid)', 'KM010');
select pg_temp.expect_sqlstate($q$insert into storage.objects(bucket_id,name,owner_id,metadata)
    values('community-posters','10000000-0000-0000-0000-000000000001/' || current_setting('test.clip') || '/poster.jpg',auth.uid()::text,'{}')$q$, '42501');
reset role;
update public.profiles set username = null where id = '10000000-0000-0000-0000-000000000001';
set local role authenticated;
select pg_temp.assert_true(jsonb_array_length(public.community_feed(p_mine => true)) = 1
    and (select count(*) = 2 from storage.objects), 'handle-cleared owner can still find and clean files');
delete from storage.objects;
select pg_temp.assert_true(public.community_finish_delete(current_setting('test.clip')::uuid)->>'deleted' = 'true', 'finish only after files removed');
select pg_temp.assert_true(jsonb_array_length(public.community_feed(p_mine => true)) = 0, 'slot released after cleanup');
select pg_temp.assert_true(public.community_finish_delete(current_setting('test.clip')::uuid)->>'deleted' = 'true', 'cleanup retry idempotent');
reset role;
update public.profiles set username = 'member_01' where id = '10000000-0000-0000-0000-000000000001';
update public.profiles set username = 'member_03' where id = '10000000-0000-0000-0000-000000000003';
set local role authenticated;

-- Full slot accounting is independent of the tiny declared byte size.
select public.community_begin_upload('Quota', '', 1, 1) from generate_series(1,5);
select pg_temp.expect_sqlstate('select public.community_begin_upload(''Quota'', '''', 1, 1)', 'KM004');
reset role;
update public.community_clips set status = 'deleting' where owner_id = '10000000-0000-0000-0000-000000000001';
set local role authenticated;
select pg_temp.expect_sqlstate('select public.community_begin_upload(''Quota'', '''', 1, 1)', 'KM004');
reset role;
do $$ declare i int; j int; begin
    for i in 2..8 loop
        perform set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-' || lpad(i::text,12,'0'), true);
        for j in 1..5 loop perform public.community_begin_upload('Capacity', '', 1, 1); end loop;
    end loop;
end $$;
set local role authenticated;
select set_config('request.jwt.claim.sub', '10000000-0000-0000-0000-000000000009', true);
select pg_temp.expect_sqlstate('select public.community_begin_upload(''Full'', '''', 1, 1)', 'KM005');
reset role;
select pg_temp.assert_true((select count(*) = 40 from public.community_clips), 'global cap includes every state');
select pg_temp.expect_sqlstate($q$delete from auth.users where id = '10000000-0000-0000-0000-000000000001'$q$, '23001');
rollback;
select 'community media integration checks passed' as result;
