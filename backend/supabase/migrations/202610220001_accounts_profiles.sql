-- Optional online accounts. Local recording and editing never query this schema.
-- Apply once as the Supabase project owner, not through a desktop client.
begin;

create schema if not exists klipp_private;
revoke all on schema klipp_private from public, anon, authenticated;

create table public.profiles (
    id uuid primary key references auth.users (id) on delete cascade,
    username text unique,
    display_name text not null default 'Player',
    bio text not null default '',
    created_at timestamptz not null default now(),
    updated_at timestamptz not null default now(),
    constraint profiles_username_format check (
        username is null or username ~ '^[a-z][a-z0-9_]{2,23}$'
    ),
    constraint profiles_display_name_length check (
        char_length(display_name) between 1 and 40
        and display_name = btrim(display_name)
        and display_name <> ''
    ),
    constraint profiles_bio_length check (char_length(bio) <= 160)
);

comment on table public.profiles is
    'Member-visible profiles. Email addresses and authentication data remain in auth.users.';
comment on column public.profiles.username is
    'NULL until the member chooses a unique handle; lowercase ASCII, 3-24 characters.';

alter table public.profiles enable row level security;

-- Explicitly remove the broad defaults found on some Supabase projects.
revoke all on table public.profiles from public, anon, authenticated;
grant usage on schema public to authenticated;
grant select (id, username, display_name, bio, created_at, updated_at)
    on public.profiles to authenticated;
grant update (username, display_name, bio) on public.profiles to authenticated;
grant select, insert, update, delete on public.profiles to service_role;

create policy profiles_read_members on public.profiles
    for select to authenticated
    using ((select auth.uid()) is not null);

create policy profiles_update_self on public.profiles
    for update to authenticated
    using ((select auth.uid()) = id)
    with check ((select auth.uid()) = id);

-- No client INSERT/DELETE policy. The signup trigger owns row creation; deleting
-- an auth user through a trusted administrator also removes its profile.
create function klipp_private.create_profile_for_user()
returns trigger
language plpgsql
security definer
set search_path = ''
as $$
begin
    -- Do not copy email, raw_user_meta_data or client-supplied privilege claims.
    insert into public.profiles (id) values (new.id);
    return new;
end;
$$;

revoke all on function klipp_private.create_profile_for_user()
    from public, anon, authenticated;

create trigger klipp_create_profile_after_signup
    after insert on auth.users
    for each row execute function klipp_private.create_profile_for_user();

create function klipp_private.touch_profile_updated_at()
returns trigger
language plpgsql
set search_path = ''
as $$
begin
    if new.id is distinct from old.id or new.created_at is distinct from old.created_at then
        raise exception 'Profile identity and creation time cannot be changed.'
            using errcode = '42501';
    end if;
    new.updated_at := statement_timestamp();
    return new;
end;
$$;

revoke all on function klipp_private.touch_profile_updated_at()
    from public, anon, authenticated;

create trigger klipp_touch_profile_before_update
    before update on public.profiles
    for each row execute function klipp_private.touch_profile_updated_at();

-- Existing project members also receive a profile when this migration is added.
insert into public.profiles (id)
    select id from auth.users
    on conflict (id) do nothing;

commit;
