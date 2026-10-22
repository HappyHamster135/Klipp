-- Run after the migration, against a LOCAL / DISPOSABLE Supabase database:
-- psql "$DATABASE_URL" -v ON_ERROR_STOP=1 -f backend/supabase/tests/accounts_profiles.sql
-- Test users, profile edits and helper functions are rolled back at the end.
begin;

create function pg_temp.assert_true(condition boolean, description text)
returns void language plpgsql as $$
begin
    if condition is not true then
        raise exception 'FAILED: %', description;
    end if;
end;
$$;

create function pg_temp.expect_sqlstate(statement text, expected_state text)
returns void language plpgsql as $$
declare
    actual_state text;
begin
    begin
        execute statement;
    exception when others then
        get stacked diagnostics actual_state = returned_sqlstate;
        if actual_state = expected_state then
            return;
        end if;
        raise exception 'Expected SQLSTATE %, got % for %', expected_state, actual_state, statement;
    end;
    raise exception 'Expected SQLSTATE %, but statement succeeded: %', expected_state, statement;
end;
$$;

insert into auth.users (id, email, raw_user_meta_data) values
    ('db4bda1a-f75f-4b83-82ce-551948c4f001', 'klipp-schema-a@example.invalid',
        '{"display_name":"Untrusted metadata","username":"claimed","role":"admin"}'),
    ('db4bda1a-f75f-4b83-82ce-551948c4f002', 'klipp-schema-b@example.invalid', '{}');

select pg_temp.assert_true(
    (select count(*) = 2 from public.profiles
     where id in ('db4bda1a-f75f-4b83-82ce-551948c4f001', 'db4bda1a-f75f-4b83-82ce-551948c4f002')
       and username is null and display_name = 'Player' and bio = ''
       and created_at is not null and updated_at is not null),
    'signup creates safe profiles without copying untrusted metadata');

select pg_temp.assert_true(
    not exists (select 1 from information_schema.columns
                where table_schema = 'public' and table_name = 'profiles'
                  and column_name in ('email', 'access_token', 'refresh_token', 'role')),
    'member profiles do not expose account secrets or email');

set local role anon;
select set_config('request.jwt.claims', '{}', true);
select pg_temp.expect_sqlstate('select * from public.profiles', '42501');
select pg_temp.expect_sqlstate('update public.profiles set bio = ''anonymous''', '42501');
select pg_temp.expect_sqlstate('select klipp_private.create_profile_for_user()', '42501');
reset role;

set local role authenticated;
select set_config('request.jwt.claims',
    '{"sub":"db4bda1a-f75f-4b83-82ce-551948c4f001","role":"authenticated"}', true);

select pg_temp.assert_true(
    (select count(*) = 2 from public.profiles
     where id in ('db4bda1a-f75f-4b83-82ce-551948c4f001', 'db4bda1a-f75f-4b83-82ce-551948c4f002')),
    'signed-in members can read member profiles');

update public.profiles set username = 'schema_player_a', display_name = 'Schema Player A', bio = 'Hello'
    where id = 'db4bda1a-f75f-4b83-82ce-551948c4f001';
select pg_temp.assert_true(
    (select username = 'schema_player_a' and display_name = 'Schema Player A' and bio = 'Hello'
     from public.profiles where id = 'db4bda1a-f75f-4b83-82ce-551948c4f001'),
    'members can edit their own visible fields');

update public.profiles set bio = 'Attempted cross-account edit'
    where id = 'db4bda1a-f75f-4b83-82ce-551948c4f002';
select pg_temp.assert_true(
    (select bio = '' from public.profiles where id = 'db4bda1a-f75f-4b83-82ce-551948c4f002'),
    'RLS prevents cross-account profile edits');

select pg_temp.expect_sqlstate(
    'update public.profiles set id = ''db4bda1a-f75f-4b83-82ce-551948c4f002'' where id = ''db4bda1a-f75f-4b83-82ce-551948c4f001''', '42501');
select pg_temp.expect_sqlstate(
    'update public.profiles set created_at = now() where id = ''db4bda1a-f75f-4b83-82ce-551948c4f001''', '42501');
select pg_temp.expect_sqlstate(
    'update public.profiles set updated_at = now() where id = ''db4bda1a-f75f-4b83-82ce-551948c4f001''', '42501');
select pg_temp.expect_sqlstate(
    'insert into public.profiles (id, username) values (''db4bda1a-f75f-4b83-82ce-551948c4f001'', ''replacement'')', '42501');
select pg_temp.expect_sqlstate(
    'delete from public.profiles where id = ''db4bda1a-f75f-4b83-82ce-551948c4f001''', '42501');

select pg_temp.expect_sqlstate(
    'update public.profiles set username = ''UPPERCASE'' where id = ''db4bda1a-f75f-4b83-82ce-551948c4f001''', '23514');
select pg_temp.expect_sqlstate(
    'update public.profiles set username = ''ab'' where id = ''db4bda1a-f75f-4b83-82ce-551948c4f001''', '23514');
select pg_temp.expect_sqlstate(
    'update public.profiles set username = repeat(''a'', 25) where id = ''db4bda1a-f75f-4b83-82ce-551948c4f001''', '23514');
select pg_temp.expect_sqlstate(
    'update public.profiles set username = ''1player'' where id = ''db4bda1a-f75f-4b83-82ce-551948c4f001''', '23514');
select pg_temp.expect_sqlstate(
    'update public.profiles set display_name = ''   '' where id = ''db4bda1a-f75f-4b83-82ce-551948c4f001''', '23514');
select pg_temp.expect_sqlstate(
    'update public.profiles set display_name = repeat(''a'', 41) where id = ''db4bda1a-f75f-4b83-82ce-551948c4f001''', '23514');
select pg_temp.expect_sqlstate(
    'update public.profiles set bio = repeat(''a'', 161) where id = ''db4bda1a-f75f-4b83-82ce-551948c4f001''', '23514');

select set_config('request.jwt.claims',
    '{"sub":"db4bda1a-f75f-4b83-82ce-551948c4f002","role":"authenticated"}', true);
select pg_temp.expect_sqlstate(
    'update public.profiles set username = ''schema_player_a'' where id = ''db4bda1a-f75f-4b83-82ce-551948c4f002''', '23505');

select set_config('request.jwt.claims', '{}', true);
select pg_temp.assert_true((select count(*) = 0 from public.profiles),
    'missing identity cannot read member profiles even with the authenticated role');
update public.profiles set bio = 'No identity';
reset role;

select pg_temp.assert_true(
    (select bio = 'Hello' from public.profiles where id = 'db4bda1a-f75f-4b83-82ce-551948c4f001'),
    'missing identity cannot edit profiles');

delete from auth.users where id = 'db4bda1a-f75f-4b83-82ce-551948c4f002';
select pg_temp.assert_true(
    not exists (select 1 from public.profiles where id = 'db4bda1a-f75f-4b83-82ce-551948c4f002'),
    'trusted auth account deletion cascades to its profile');

rollback;
select 'Klipp account/profile access checks passed.' as result;
