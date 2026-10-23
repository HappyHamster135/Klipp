-- Optional online operations. Recording/editing remain local and anonymous.
-- Provider counters are unknown until explicitly observed, never invented zero.
begin;
alter table public.community_clips add column moderation_hidden boolean not null default false;
alter table public.community_reports add column id uuid not null default gen_random_uuid(),
    add column review_note text not null default '' check(char_length(review_note)<=1000),
    add column reviewed_at timestamptz;
create unique index community_reports_id on public.community_reports(id);
create index community_reports_review_queue on public.community_reports(status,created_at desc,id desc);

create table klipp_private.community_operators(
    user_id uuid primary key references auth.users(id) on delete cascade,
    enabled boolean not null default true,
    created_at timestamptz not null default clock_timestamp()
);
create table klipp_private.community_operations_config(
    singleton boolean primary key default true check(singleton),
    operations_enabled boolean not null default false,
    account_deletion_enabled boolean not null default false
);
insert into klipp_private.community_operations_config(singleton)values(true);
create table klipp_private.community_account_deletions(
    owner_id uuid primary key references auth.users(id) on delete cascade,
    created_at timestamptz not null default clock_timestamp(),
    updated_at timestamptz not null default clock_timestamp()
);
create table klipp_private.community_support(
    id uuid primary key default gen_random_uuid(),
    owner_id uuid not null references auth.users(id) on delete cascade,
    category text not null check(category in('help','privacy','deletion')),
    body text not null check(char_length(body) between 10 and 2000 and body=btrim(body)),
    status text not null default 'open' check(status in('open','resolved')),
    reply text not null default '' check(char_length(reply)<=2000),
    created_at timestamptz not null default clock_timestamp(),
    updated_at timestamptz not null default clock_timestamp()
);
create index community_support_owner on klipp_private.community_support(owner_id,created_at desc,id desc);
create index community_support_queue on klipp_private.community_support(status,created_at desc,id desc);
create table klipp_private.community_operation_events(
    id uuid primary key default gen_random_uuid(),
    actor_id uuid references auth.users(id) on delete set null,
    action text not null check(action in('hide','keep','support_reply')),
    clip_id uuid references public.community_clips(id) on delete set null,
    request_id uuid references klipp_private.community_support(id) on delete set null,
    note text not null check(char_length(note) between 1 and 2000),
    created_at timestamptz not null default clock_timestamp()
);
create index community_operation_events_created on klipp_private.community_operation_events(created_at);
create index community_operation_events_actor on klipp_private.community_operation_events(actor_id);
create index community_operation_events_clip on klipp_private.community_operation_events(clip_id);
create index community_operation_events_request on klipp_private.community_operation_events(request_id);
create table klipp_private.community_provider_usage(
    metric text primary key check(metric in('cached_egress','uncached_egress','monthly_active_users','email_daily','email_monthly')),
    used bigint check(used>=0),
    quota bigint not null check(quota>0),
    period_start timestamptz,period_end timestamptz,observed_at timestamptz,
    source text check(char_length(source)<=120),
    check(used is null or(period_start is not null and period_end>period_start and observed_at is not null))
);
insert into klipp_private.community_provider_usage(metric,quota,source) values
    ('cached_egress',5000000000,'Supabase dashboard; not connected'),
    ('uncached_egress',5000000000,'Supabase dashboard; not connected'),
    ('monthly_active_users',50000,'Supabase dashboard; not connected'),
    ('email_daily',100,'Resend dashboard; not connected'),
    ('email_monthly',3000,'Resend dashboard; not connected');
do $$declare t text; begin
    foreach t in array array['community_operators','community_operations_config','community_account_deletions','community_support','community_operation_events','community_provider_usage'] loop
        execute format('alter table klipp_private.%I enable row level security',t);
        execute format('revoke all on klipp_private.%I from public,anon,authenticated,service_role',t);
        execute format('create policy %I on klipp_private.%I as restrictive for all to anon,authenticated using(false) with check(false)',t||'_private',t);
    end loop;
end $$;

create function klipp_private.community_sensitive_member_id()
returns uuid language plpgsql stable security definer set search_path='' as $$
declare v_actor uuid:=klipp_private.community_member_id(false); v_session text:=auth.jwt()->>'session_id';
begin
    if v_actor is null or v_session is null or v_session !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$' then return null; end if;
    if not exists(select 1 from auth.sessions where id=v_session::uuid and user_id=v_actor and(not_after is null or not_after>now())) then return null; end if;
    return v_actor;
end $$;
create function klipp_private.community_is_operator()
returns boolean language sql stable security definer set search_path='' as $$
    select exists(select 1 from klipp_private.community_operators where user_id=klipp_private.community_sensitive_member_id() and enabled)
$$;
create function klipp_private.community_active_member()
returns boolean language sql stable security definer set search_path='' as $$
    select klipp_private.community_member_id(false) is not null and not exists(
        select 1 from klipp_private.community_account_deletions where owner_id=auth.uid())
$$;
create function klipp_private.community_existing_member()
returns boolean language sql stable security definer set search_path='' as $$
    select klipp_private.community_member_id(false) is not null
$$;
revoke all on function klipp_private.community_sensitive_member_id(),klipp_private.community_is_operator(),klipp_private.community_active_member(),klipp_private.community_existing_member()
    from public,anon,authenticated;
grant execute on function klipp_private.community_active_member(),klipp_private.community_existing_member() to authenticated;
-- Pending members can still read their own current handle to retry confirmation.
-- Deleted-user JWTs cannot read member profiles until token expiry.
alter policy profiles_read_members on public.profiles using((select klipp_private.community_existing_member())
    and((select auth.uid())=id or(select klipp_private.community_active_member())));
alter policy profiles_update_self on public.profiles using((select auth.uid())=id and(select klipp_private.community_active_member()))
    with check((select auth.uid())=id and(select klipp_private.community_active_member()));

create function klipp_private.community_support_json(p_id uuid,p_operator boolean)
returns jsonb language sql stable security definer set search_path='' as $$
    select jsonb_build_object('id',s.id,'category',s.category,'body',s.body,'status',s.status,'reply',s.reply,
        'created_at',s.created_at,'updated_at',s.updated_at)
        ||case when p_operator then jsonb_build_object('owner_id',s.owner_id,'username',p.username) else '{}'::jsonb end
    from klipp_private.community_support s join public.profiles p on p.id=s.owner_id where s.id=p_id
$$;
revoke all on function klipp_private.community_support_json(uuid,boolean) from public,anon,authenticated;

create function klipp_private.community_operations(p_action text,p_args jsonb)
returns jsonb language plpgsql security definer set search_path='' as $$
declare
    v_actor uuid:=klipp_private.community_member_id(false); v_operator boolean;
    v_id uuid; v_limit integer; v_before timestamptz; v_before_id uuid; v_body text; v_note text;
    v_result jsonb; v_home bigint; v_cloud bigint; v_unknown bigint; v_own bigint; v_count bigint; v_hidden boolean;
begin
    if v_actor is null then raise exception 'A verified permanent account is required.' using errcode='KM001'; end if;
    v_operator:=klipp_private.community_is_operator();
    if p_action like 'operator_%' and not v_operator then raise exception 'Operator access is required.' using errcode='KO001'; end if;
    if p_action='capabilities' then return(select jsonb_build_object('enabled',operations_enabled,'operations_enabled',operations_enabled,
        'version',1,'account_deletion',operations_enabled and account_deletion_enabled,'operator',v_operator)from klipp_private.community_operations_config where singleton);end if;
    if not exists(select 1 from klipp_private.community_operations_config where singleton and operations_enabled)then
        raise exception 'Online operations are getting ready.'using errcode='KO004';end if;
    if p_action='usage' then
        select coalesce(sum(byte_size+coalesce(poster_byte_size,0)),0) into v_home from public.community_clips where storage_provider='home' and status='published';
        select coalesce(sum(case when metadata->>'size' ~ '^[0-9]{1,12}$' then(metadata->>'size')::bigint else 0 end),0),
            count(*) filter(where not coalesce(metadata->>'size' ~ '^[0-9]{1,12}$',false)) into v_cloud,v_unknown
            from storage.objects where bucket_id in('community-videos','community-posters');
        select coalesce(sum(c.byte_size+coalesce(c.poster_byte_size,0)) filter(where c.storage_provider='home' and c.status='published'),0)
            +coalesce((select sum(case when o.metadata->>'size' ~ '^[0-9]{1,12}$' then(o.metadata->>'size')::bigint else 0 end)
                from storage.objects o where o.bucket_id in('community-videos','community-posters') and o.owner_id=v_actor::text),0)
            into v_own from public.community_clips c where c.owner_id=v_actor;
        select jsonb_build_object('slots_used',count(*),'slots_limit',5,'published',count(*) filter(where status='published'),
            'uploading',count(*) filter(where status='uploading'),'deleting',count(*) filter(where status='deleting'),'stored_bytes',v_own)
            into v_result from public.community_clips where owner_id=v_actor;
        return jsonb_build_object('me',v_result,'beta',jsonb_build_object('slots_used',(select count(*) from public.community_clips),'slots_limit',40,
            'reserved_bytes',(select count(*) from public.community_clips)*17039360,'storage_bytes',v_home+v_cloud,'storage_limit_bytes',681574400,
            'unknown_storage_objects',v_unknown,'home_bytes',v_home,'legacy_bytes',v_cloud,'storage_scope','tracked_media_budget'),
            'limits',jsonb_build_object('max_video_bytes',16777216,'max_duration_seconds',60,'warning_percent',80),
            'operator',v_operator,'checked_at',clock_timestamp(),'operations',case when v_operator then jsonb_build_object(
                'registered_users',(select count(*) from auth.users),'database_bytes',pg_database_size(current_database()),'database_limit_bytes',500000000,
                'open_reports',(select count(*) from public.community_reports where status='open'),
                'open_support',(select count(*) from klipp_private.community_support where status='open'),
                'provider_usage',(select jsonb_object_agg(metric,jsonb_build_object('used',used,'limit',quota,'period_start',period_start,
                    'period_end',period_end,'observed_at',observed_at,'source',source)) from klipp_private.community_provider_usage)) else null end);
    end if;
    if p_action in('support','operator_support','operator_reports') then
        v_limit:=coalesce((p_args->>'limit')::integer,case when p_action='support' then 100 else 26 end);
        v_before:=(p_args->>'before')::timestamptz;v_before_id:=(p_args->>'before_id')::uuid;
        if v_limit not between 1 and (case when p_action='support' then 100 else 26 end) or((v_before is null)<>(v_before_id is null)) then
            raise exception 'Invalid page cursor.' using errcode='KM003';end if;
        if p_action='operator_reports' then
            select coalesce(jsonb_agg(q.item order by q.created_at desc,q.id desc),'[]'::jsonb) into v_result from(
                select r.id,r.created_at,jsonb_build_object('id',r.id,'clip_id',r.clip_id,'reporter_id',r.reporter_id,'reason',r.reason,
                    'status',r.status,'created_at',r.created_at,'title',c.title,'owner_username',p.username,'reporter_username',rp.username,
                    'hidden',c.moderation_hidden,'review_note',r.review_note,'reviewed_at',r.reviewed_at) item
                from public.community_reports r join public.community_clips c on c.id=r.clip_id join public.profiles p on p.id=c.owner_id
                join public.profiles rp on rp.id=r.reporter_id where(v_before is null or(r.created_at,r.id)<(v_before,v_before_id))
                order by r.created_at desc,r.id desc limit v_limit)q;return v_result;
        end if;
        select coalesce(jsonb_agg(q.item order by q.created_at desc,q.id desc),'[]'::jsonb) into v_result from(
            select s.id,s.created_at,klipp_private.community_support_json(s.id,p_action='operator_support') item
            from klipp_private.community_support s where(p_action='operator_support' or s.owner_id=v_actor)
                and(v_before is null or(s.created_at,s.id)<(v_before,v_before_id))order by s.created_at desc,s.id desc limit v_limit)q;
        return v_result;
    end if;
    if not klipp_private.community_active_member() then raise exception 'Account deletion is pending. Retry deletion to finish cleanup.' using errcode='KO004';end if;
    if p_action='submit_support' then
        v_body:=btrim(coalesce(p_args->>'body',''));
        if not coalesce(p_args->>'category' in('help','privacy','deletion'),false) or char_length(v_body) not between 10 and 2000 then
            raise exception 'Invalid support request.' using errcode='KO002';end if;
        perform pg_advisory_xact_lock(714,1230);
        if(select count(*) from klipp_private.community_support where owner_id=v_actor and created_at>=now()-interval '1 day')>=3
            or(select count(*) from klipp_private.community_support where owner_id=v_actor)>=100
            or(select count(*) from klipp_private.community_support)>=2000 then
            raise exception 'Please wait before sending another request.' using errcode='KO003';end if;
        insert into klipp_private.community_support(owner_id,category,body)values(v_actor,p_args->>'category',v_body)returning id into v_id;
        return klipp_private.community_support_json(v_id,false);
    elsif p_action in('operator_review','operator_reply') then
        v_note:=btrim(coalesce(p_args->>'note',p_args->>'reply',''));
        if char_length(v_note) not between 1 and (case when p_action='operator_review' then 1000 else 2000 end) then
            raise exception 'Write a review or reply.' using errcode='KO002';end if;
        perform pg_advisory_xact_lock(714,1231);
        delete from klipp_private.community_operation_events where created_at<now()-interval '90 days';
        if(select count(*) from klipp_private.community_operation_events)>=5000 then raise exception 'Review history needs maintenance.' using errcode='KO003';end if;
        if p_action='operator_review' then
            v_id:=(p_args->>'clip_id')::uuid;
            if not coalesce(p_args->>'action' in('hide','keep'),false) then raise exception 'Invalid review action.' using errcode='KO002';end if;
            perform 1 from public.community_clips where id=v_id and status='published' for update;
            if not found then raise exception 'Clip unavailable.' using errcode='KM007';end if;
            v_hidden:=(p_args->>'action'='hide');
            update public.community_clips set moderation_hidden=v_hidden where id=v_id;
            update public.community_reports set status='resolved',review_note=v_note,reviewed_at=clock_timestamp() where clip_id=v_id;
            insert into klipp_private.community_operation_events(actor_id,action,clip_id,note)values(v_actor,p_args->>'action',v_id,v_note);
            return jsonb_build_object('reviewed',true,'hidden',v_hidden);
        end if;
        v_id:=(p_args->>'request_id')::uuid;
        if not coalesce(p_args->>'status' in('open','resolved'),false) then raise exception 'Invalid support status.' using errcode='KO002';end if;
        update klipp_private.community_support set reply=v_note,status=p_args->>'status',updated_at=clock_timestamp() where id=v_id;
        if not found then raise exception 'Request unavailable.' using errcode='KO002';end if;
        -- Do not duplicate private support conversation text into retained audit.
        insert into klipp_private.community_operation_events(actor_id,action,request_id,note)
            values(v_actor,'support_reply',v_id,'Support reply updated; status '||(p_args->>'status')||'.');
        return klipp_private.community_support_json(v_id,true);
    end if;
    raise exception 'Unknown operation.' using errcode='KO002';
end $$;
revoke all on function klipp_private.community_operations(text,jsonb) from public,anon,authenticated;
grant execute on function klipp_private.community_operations(text,jsonb) to authenticated;

create function public.community_operations_capabilities() returns jsonb language sql security invoker set search_path='' as $$
    select klipp_private.community_operations('capabilities','{}') $$;
create function public.community_usage() returns jsonb language sql security invoker set search_path='' as $$
    select klipp_private.community_operations('usage','{}') $$;
create function public.community_support_requests(p_limit integer default 100,p_before timestamptz default null,p_before_id uuid default null)
returns jsonb language sql security invoker set search_path='' as $$
    select klipp_private.community_operations('support',jsonb_build_object('limit',p_limit,'before',p_before,'before_id',p_before_id)) $$;
create function public.community_submit_support(p_category text,p_body text) returns jsonb language sql security invoker set search_path='' as $$
    select klipp_private.community_operations('submit_support',jsonb_build_object('category',p_category,'body',p_body)) $$;
create function public.community_operator_support(p_limit integer default 26,p_before timestamptz default null,p_before_id uuid default null)
returns jsonb language sql security invoker set search_path='' as $$
    select klipp_private.community_operations('operator_support',jsonb_build_object('limit',p_limit,'before',p_before,'before_id',p_before_id)) $$;
create function public.community_operator_reports(p_limit integer default 26,p_before timestamptz default null,p_before_id uuid default null)
returns jsonb language sql security invoker set search_path='' as $$
    select klipp_private.community_operations('operator_reports',jsonb_build_object('limit',p_limit,'before',p_before,'before_id',p_before_id)) $$;
create function public.community_operator_reply(p_request_id uuid,p_reply text,p_status text) returns jsonb language sql security invoker set search_path='' as $$
    select klipp_private.community_operations('operator_reply',jsonb_build_object('request_id',p_request_id,'reply',p_reply,'status',p_status)) $$;
create function public.community_operator_review(p_clip_id uuid,p_action text,p_note text) returns jsonb language sql security invoker set search_path='' as $$
    select klipp_private.community_operations('operator_review',jsonb_build_object('clip_id',p_clip_id,'action',p_action,'note',p_note)) $$;

create function klipp_private.community_prepare_account_delete(p_confirmation text)
returns jsonb language plpgsql security definer set search_path='' as $$
declare v_actor uuid:=klipp_private.community_sensitive_member_id();v_expected text;v_result jsonb;
begin
    if v_actor is null then raise exception 'Sign in again before deleting your account.' using errcode='KM001';end if;
    if not exists(select 1 from klipp_private.community_operations_config where singleton and operations_enabled and account_deletion_enabled)then
        raise exception 'Account deletion is getting ready.'using errcode='KO004';end if;
    select coalesce(username,'DELETE') into v_expected from public.profiles where id=v_actor for update;
    if p_confirmation is distinct from v_expected then raise exception 'Type your current username exactly.' using errcode='KO002';end if;
    perform pg_advisory_xact_lock(714,1221);
    if(select count(*) from public.community_clips where owner_id=v_actor)>5 then raise exception 'Account cleanup requires review.' using errcode='KO004';end if;
    insert into klipp_private.community_account_deletions(owner_id)values(v_actor)
        on conflict(owner_id)do update set updated_at=clock_timestamp();
    update public.community_clips set status='deleting' where owner_id=v_actor;
    select coalesce(jsonb_agg(jsonb_build_object('id',id,'owner_id',owner_id,'storage_provider',storage_provider,
        'video_path',video_path,'poster_path',poster_path)order by id),'[]'::jsonb)into v_result from public.community_clips where owner_id=v_actor;
    return jsonb_build_object('pending',true,'owner_id',v_actor,'clips',v_result);
end $$;
create function public.community_prepare_account_delete(p_confirmation text)returns jsonb language sql security invoker set search_path='' as $$
    select klipp_private.community_prepare_account_delete(p_confirmation) $$;
revoke all on function klipp_private.community_prepare_account_delete(text),public.community_prepare_account_delete(text)from public,anon,authenticated;
grant execute on function klipp_private.community_prepare_account_delete(text),public.community_prepare_account_delete(text)to authenticated;

-- This final readiness proof is callable only inside the server-side Edge
-- function. It does not delete Auth data, grant a caller-chosen identity, or
-- release a single clip slot. Auth admin deletion follows only after ready.
create function klipp_private.community_account_delete_ready(p_owner_id uuid)
returns jsonb language plpgsql security definer set search_path='' as $$
begin
    if p_owner_id is null or not exists(select 1 from klipp_private.community_account_deletions where owner_id=p_owner_id)then
        raise exception 'Account deletion was not confirmed.' using errcode='KO004';end if;
    perform pg_advisory_xact_lock(714,1221);
    if exists(select 1 from public.community_clips where owner_id=p_owner_id)
        or exists(select 1 from storage.objects where owner_id=p_owner_id::text or owner=p_owner_id)then
        raise exception 'Account files still need cleanup.' using errcode='KO004';end if;
    return jsonb_build_object('ready',true,'owner_id',p_owner_id);
end $$;
create function public.community_account_delete_ready(p_owner_id uuid)returns jsonb language sql security invoker set search_path='' as $$
    select klipp_private.community_account_delete_ready(p_owner_id) $$;
revoke all on function klipp_private.community_account_delete_ready(uuid),public.community_account_delete_ready(uuid)from public,anon,authenticated,service_role;
grant usage on schema klipp_private to service_role;
grant execute on function klipp_private.community_account_delete_ready(uuid),public.community_account_delete_ready(uuid)to service_role;

create function klipp_private.community_home_asset_allowed(p_clip_id uuid,p_owner_id uuid)
returns jsonb language plpgsql security definer set search_path='' as $$
begin
    if not coalesce(klipp_private.community_home_maintenance_allowed(),false)then
        raise exception 'The media server must verify playback access.' using errcode='KM015';end if;
    return jsonb_build_object('allowed',exists(select 1 from public.community_clips c join auth.users u on u.id=c.owner_id
        join public.profiles p on p.id=c.owner_id where c.id=p_clip_id and c.owner_id=p_owner_id and c.storage_provider='home'
        and c.status='published' and not c.moderation_hidden and p.username is not null and u.email_confirmed_at is not null
        and not coalesce(u.is_anonymous,false)and(u.banned_until is null or u.banned_until<=now())
        and not exists(select 1 from klipp_private.community_account_deletions where owner_id=c.owner_id)));
end $$;
create function public.community_home_asset_allowed(p_clip_id uuid,p_owner_id uuid)returns jsonb language sql security invoker set search_path='' as $$
    select klipp_private.community_home_asset_allowed(p_clip_id,p_owner_id) $$;
revoke all on function klipp_private.community_home_asset_allowed(uuid,uuid),public.community_home_asset_allowed(uuid,uuid)from public,anon,authenticated;
grant execute on function klipp_private.community_home_asset_allowed(uuid,uuid),public.community_home_asset_allowed(uuid,uuid)to anon,authenticated;

revoke all on function public.community_operations_capabilities(),public.community_usage(),
    public.community_support_requests(integer,timestamptz,uuid),public.community_submit_support(text,text),
    public.community_operator_support(integer,timestamptz,uuid),public.community_operator_reports(integer,timestamptz,uuid),
    public.community_operator_reply(uuid,text,text),public.community_operator_review(uuid,text,text)from public,anon,authenticated;
grant execute on function public.community_operations_capabilities(),public.community_usage(),
    public.community_support_requests(integer,timestamptz,uuid),public.community_submit_support(text,text),
    public.community_operator_support(integer,timestamptz,uuid),public.community_operator_reports(integer,timestamptz,uuid),
    public.community_operator_reply(uuid,text,text),public.community_operator_review(uuid,text,text)to authenticated;



create or replace function klipp_private.community_dispatch(p_action text, p_args jsonb)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare
    v_actor uuid;
    v_id uuid;
    v_other uuid;
    v_clip public.community_clips%rowtype;
    v_comment public.community_comments%rowtype;
    v_limit integer;
    v_before timestamptz;
    v_before_id uuid;
    v_body text;
    v_result jsonb;
    v_now timestamptz := clock_timestamp();
begin
    if p_action in ('begin', 'publish') then
        raise exception 'Update Klipp on your PC to upload through the media server.' using errcode = 'KM018';
    end if;
    -- Cleanup remains possible if a member clears their handle after uploading.
    v_actor := klipp_private.community_member_id(false);
    if v_actor is null then
        raise exception 'A verified permanent account is required.' using errcode = 'KM001';
    end if;
    if p_action not in ('remove', 'finish', 'get', 'blocked')
        and not (p_action = 'feed' and coalesce((p_args->>'mine')::boolean, false))
        and not (p_action = 'block' and not coalesce((p_args->>'blocked')::boolean, false))
        and klipp_private.community_member_id(true) is null then
        raise exception 'Choose a username first.' using errcode = 'KM002';
    end if;
    if exists(select 1 from klipp_private.community_account_deletions where owner_id=v_actor)
        and p_action not in ('get','remove','finish')
        and not(p_action='feed' and coalesce((p_args->>'mine')::boolean,false))then
        raise exception 'Account deletion is pending. Retry deletion to finish cleanup.' using errcode='KO004';
    end if;
    v_id := (p_args->>'id')::uuid;

    if p_action in ('feed', 'comments') then
        v_limit := coalesce((p_args->>'limit')::integer, case when p_action = 'feed' then 13 else 31 end);
        v_before := (p_args->>'before')::timestamptz;
        v_before_id := (p_args->>'before_id')::uuid;
        if v_limit not between 1 and 50 or ((v_before is null) <> (v_before_id is null)) then
            raise exception 'Invalid page cursor.' using errcode = 'KM003';
        end if;
        if p_action = 'feed' then
            select coalesce(jsonb_agg(q.item order by q.created_at desc, q.id desc), '[]'::jsonb) into v_result
            from (select c.id, c.created_at, klipp_private.community_clip_json(c.id, v_actor) item
                from public.community_clips c join public.profiles cp on cp.id = c.owner_id
                where ((coalesce((p_args->>'mine')::boolean, false) and c.owner_id = v_actor)
                    or (not coalesce((p_args->>'mine')::boolean, false) and c.status = 'published' and not c.moderation_hidden and cp.username is not null))
                  and not klipp_private.community_blocked(v_actor, c.owner_id)
                  and (v_before is null or (c.created_at, c.id) < (v_before, v_before_id))
                order by c.created_at desc, c.id desc limit v_limit) q;
            return v_result;
        end if;
    end if;

    if p_action = 'blocked' then
        select coalesce(jsonb_agg(q.item order by q.display_name, q.user_id), '[]'::jsonb) into v_result
        from (select p.id user_id, p.display_name,
            jsonb_build_object('user_id', p.id, 'username', coalesce(p.username, ''), 'display_name', p.display_name) item
            from public.community_blocks b join public.profiles p on p.id = b.blocked_id
            where b.blocker_id = v_actor order by p.display_name, p.id limit 100) q;
        return v_result;
    end if;
    if p_action in ('remove', 'finish') then
        select * into v_clip from public.community_clips where id = v_id for update;
    elsif p_action not in ('delete_comment', 'block') then
        select * into v_clip from public.community_clips where id = v_id;
    end if;
    if p_action in ('remove', 'finish') then
        if v_clip.id is null then
            return jsonb_build_object('id', v_id, 'status', 'deleted', 'deleted', true);
        end if;
        if v_clip.owner_id <> v_actor then
            raise exception 'Only the owner can remove this clip.' using errcode = 'KM011';
        end if;
        if p_action = 'remove' then
            update public.community_clips set status = 'deleting' where id = v_id;
            return klipp_private.community_clip_json(v_id, v_actor);
        end if;
        if v_clip.status <> 'deleting' then
            raise exception 'Remove the clip before finishing cleanup.' using errcode = 'KM010';
        end if;
        if v_clip.storage_provider = 'home' and not coalesce(klipp_private.community_home_gateway_allowed(), false) then
            raise exception 'The media server must confirm file cleanup.' using errcode = 'KM015';
        end if;
        if exists(select 1 from storage.objects o where
            (o.bucket_id = 'community-videos' and o.name = v_clip.video_path)
            or (o.bucket_id = 'community-posters' and o.name = v_clip.poster_path)) then
            raise exception 'Uploaded files still need cleanup.' using errcode = 'KM010';
        end if;
        delete from public.community_clips where id = v_id;
        return jsonb_build_object('id', v_id, 'deleted', true);
    elsif p_action = 'delete_comment' then
        select * into v_comment from public.community_comments where id = v_id for update;
        if v_comment.id is null then return jsonb_build_object('deleted', true); end if;
        if v_comment.owner_id <> v_actor then
            raise exception 'Only the author can delete a comment.' using errcode = 'KM011';
        end if;
        delete from public.community_comments where id = v_id;
        return jsonb_build_object('deleted', true);

    elsif p_action = 'block' then
        v_other := (p_args->>'user_id')::uuid;
        if v_other is null or v_other = v_actor or not exists(select 1 from public.profiles where id = v_other) then
            raise exception 'Invalid member.' using errcode = 'KM003';
        end if;
        perform pg_advisory_xact_lock(714, 1222);
        if coalesce((p_args->>'blocked')::boolean, false) then
            if not exists(select 1 from public.community_blocks where blocker_id = v_actor and blocked_id = v_other)
                and (select count(*) from public.community_blocks where blocker_id = v_actor) >= 100 then
                raise exception 'Block list limit reached.' using errcode = 'KM009';
            end if;
            insert into public.community_blocks(blocker_id, blocked_id) values(v_actor, v_other) on conflict do nothing;
        else
            delete from public.community_blocks where blocker_id = v_actor and blocked_id = v_other;
        end if;
        return jsonb_build_object('blocked', coalesce((p_args->>'blocked')::boolean, false));
    end if;

    if v_clip.moderation_hidden and not(p_action='get' and v_clip.owner_id=v_actor)then
        raise exception 'Clip unavailable.' using errcode='KM007';
    end if;
    if v_clip.id is null or (v_clip.status <> 'published' and not (p_action = 'get' and v_clip.owner_id = v_actor))
        or klipp_private.community_blocked(v_actor, v_clip.owner_id)
        or (v_clip.owner_id <> v_actor and (klipp_private.community_member_id(true) is null
            or not exists(select 1 from public.profiles where id = v_clip.owner_id and username is not null))) then
        raise exception 'Clip unavailable.' using errcode = 'KM007';
    end if;
    if p_action = 'get' then return klipp_private.community_clip_json(v_id, v_actor);
    elsif p_action = 'like' then
        if p_args->>'liked' is null then raise exception 'Choose a like state.' using errcode = 'KM003'; end if;
        if (p_args->>'liked')::boolean then
            insert into public.community_likes(clip_id, owner_id) values(v_id, v_actor) on conflict do nothing;
        else
            delete from public.community_likes where clip_id = v_id and owner_id = v_actor;
        end if;
        return klipp_private.community_clip_json(v_id, v_actor);
    elsif p_action = 'comments' then
        select coalesce(jsonb_agg(q.item order by q.created_at desc, q.id desc), '[]'::jsonb) into v_result
        from (select m.id, m.created_at, klipp_private.community_comment_json(m.id) item
            from public.community_comments m join public.profiles mp on mp.id = m.owner_id where m.clip_id = v_id and mp.username is not null
              and not klipp_private.community_blocked(v_actor, m.owner_id)
              and (v_before is null or (m.created_at, m.id) < (v_before, v_before_id))
            order by m.created_at desc, m.id desc limit v_limit) q;
        return v_result;
    elsif p_action = 'comment' then
        v_body := btrim(coalesce(p_args->>'body', ''));
        if char_length(v_body) not between 1 and 500 then
            raise exception 'Comments must contain 1–500 characters.' using errcode = 'KM003';
        end if;
        perform pg_advisory_xact_lock(714, 1223);
        delete from klipp_private.community_comment_events where created_at < v_now - interval '1 hour';
        if (select count(*) from klipp_private.community_comment_events where owner_id = v_actor
                and created_at >= v_now - interval '1 minute') >= 5
            or (select count(*) from klipp_private.community_comment_events where owner_id = v_actor) >= 30 then
            raise exception 'Please wait before commenting again.' using errcode = 'KM008';
        end if;
        if (select count(*) from public.community_comments where clip_id = v_id) >= 100 then
            raise exception 'This clip has reached its comment limit.' using errcode = 'KM009';
        end if;
        insert into public.community_comments(clip_id, owner_id, body) values(v_id, v_actor, v_body) returning id into v_other;
        insert into klipp_private.community_comment_events(owner_id) values(v_actor);
        return klipp_private.community_comment_json(v_other);
    elsif p_action = 'report' then
        v_body := btrim(coalesce(p_args->>'reason', ''));
        if char_length(v_body) not between 1 and 500 then
            raise exception 'Reports must contain 1–500 characters.' using errcode = 'KM003';
        end if;
        perform pg_advisory_xact_lock(714, 1224);
        if not exists(select 1 from public.community_reports where clip_id = v_id and reporter_id = v_actor) then
            if (select count(*) from public.community_reports where reporter_id = v_actor
                    and created_at >= v_now - interval '1 day') >= 20 then
                raise exception 'Report limit reached. Please try later.' using errcode = 'KM008';
            end if;
            if (select count(*) from public.community_reports) >= 2000 then
                raise exception 'Report capacity reached.' using errcode = 'KM009';
            end if;
            insert into public.community_reports(clip_id, reporter_id, reason) values(v_id, v_actor, v_body);
        end if;
        return jsonb_build_object('reported', true);
    end if;
    raise exception 'Unknown media action.' using errcode = 'KM003';
end;
$$;

create or replace function klipp_private.community_clip_json(p_id uuid, p_viewer uuid)
returns jsonb language sql stable security definer set search_path = '' as $$
    select jsonb_build_object(
        'id', c.id, 'owner_id', c.owner_id, 'username', p.username, 'display_name', p.display_name,
        'title', c.title, 'description', c.description, 'duration_seconds', c.duration_seconds,
        'byte_size', c.byte_size, 'created_at', c.created_at, 'status', c.status,
        'upload_expires_at', c.upload_expires_at, 'moderation_hidden', c.moderation_hidden,
        'video_path', c.video_path, 'poster_path', c.poster_path, 'storage_provider', c.storage_provider,
        'like_count', (select count(*) from public.community_likes l where l.clip_id = c.id),
        'comment_count', (select count(*) from public.community_comments m join public.profiles mp on mp.id = m.owner_id
            where m.clip_id = c.id and mp.username is not null and not klipp_private.community_blocked(p_viewer, m.owner_id)),
        'liked_by_me', exists(select 1 from public.community_likes l where l.clip_id = c.id and l.owner_id = p_viewer))
    from public.community_clips c join public.profiles p on p.id = c.owner_id where c.id = p_id
$$;

create or replace function klipp_private.community_begin_home_upload(
    p_title text, p_description text, p_duration_seconds double precision, p_byte_size bigint)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_actor uuid; v_id uuid; v_title text := btrim(coalesce(p_title, '')); v_now timestamptz;
begin
    if not klipp_private.community_active_member() then
        if klipp_private.community_member_id(false) is null then raise exception 'A verified permanent account is required.' using errcode='KM001';end if;
        raise exception 'Account deletion is pending. Retry deletion to finish cleanup.' using errcode='KO004';
    end if;
    if not klipp_private.community_upload_client_allowed() then
        raise exception 'Upload clips from Klipp on your PC.' using errcode = 'KM014';
    end if;
    v_actor := klipp_private.community_member_id(false);
    if v_actor is null then raise exception 'A verified permanent account is required.' using errcode = 'KM001'; end if;
    if klipp_private.community_member_id(true) is null then raise exception 'Choose a username first.' using errcode = 'KM002'; end if;
    if char_length(v_title) not between 1 and 120 or char_length(coalesce(p_description, '')) > 500
      or not coalesce(p_duration_seconds > 0 and p_duration_seconds <= 60, false)
      or not coalesce(p_byte_size between 1 and 16777216, false) then
        raise exception 'Invalid clip details.' using errcode = 'KM003';
    end if;
    if not exists(select 1 from klipp_private.community_home_config where singleton and uploads_enabled and gateway_secret_sha256 is not null) then
        raise exception 'Media uploads are temporarily unavailable.' using errcode = 'KM016';
    end if;
    perform pg_advisory_xact_lock(714, 1221);
    -- Deletion preparation uses this same reservation lock. Revalidate only
    -- after acquiring it as well: a request waiting behind confirmation must
    -- not create a new clip from its earlier active-member snapshot.
    if not klipp_private.community_active_member()then
        raise exception 'Account deletion is pending. Retry deletion to finish cleanup.' using errcode='KO004';
    end if;
    v_now := clock_timestamp();
    -- Expired/deleting reservations retain complete slots until the gateway
    -- confirms physical deletion. Releasing them sooner creates orphan files.
    if (select count(*) from public.community_clips where owner_id = v_actor) >= 5 then
        raise exception 'Your five upload slots are in use.' using errcode = 'KM004';
    end if;
    if (select count(*) from public.community_clips) >= 40 then
        raise exception 'The free beta upload capacity is full.' using errcode = 'KM005';
    end if;
    if (select count(*) from public.community_clips where owner_id = v_actor and storage_provider = 'home' and status in ('uploading', 'deleting')) >= 2
      or (select count(*) from public.community_clips where storage_provider = 'home' and status in ('uploading', 'deleting')) >= 10 then
        raise exception 'Please wait for an upload to finish before starting another.' using errcode = 'KM019';
    end if;
    delete from klipp_private.community_upload_events where created_at < v_now - interval '1 day';
    if (select count(*) from klipp_private.community_upload_events where owner_id = v_actor and created_at >= v_now - interval '1 hour') >= 5
      or (select count(*) from klipp_private.community_upload_events where owner_id = v_actor) >= 20
      or (select count(*) from klipp_private.community_upload_events where created_at >= v_now - interval '1 hour') >= 100
      or (select count(*) from klipp_private.community_upload_events) >= 300 then
        raise exception 'Please wait before uploading again.' using errcode = 'KM008';
    end if;
    insert into public.community_clips(owner_id, title, description, duration_seconds, byte_size, storage_provider, upload_expires_at)
        values(v_actor, v_title, coalesce(p_description, ''), p_duration_seconds, p_byte_size, 'home', v_now + interval '30 minutes')
        returning id into v_id;
    insert into klipp_private.community_upload_events(owner_id, created_at) values(v_actor, v_now);
    return klipp_private.community_clip_json(v_id, v_actor);
end;
$$;

create or replace function klipp_private.community_home_publish(
    p_clip_id uuid, p_video_bytes bigint, p_poster_bytes bigint, p_duration_seconds double precision,
    p_video_sha256 text, p_poster_sha256 text)
returns jsonb language plpgsql security definer set search_path = '' as $$
declare v_actor uuid; v_clip public.community_clips%rowtype;
begin
    v_actor := klipp_private.community_member_id(false);
    if v_actor is null then raise exception 'A verified permanent account is required.' using errcode = 'KM001'; end if;
    if not klipp_private.community_active_member()then raise exception 'Account deletion is pending.' using errcode='KO004';end if;
    if not coalesce(klipp_private.community_home_gateway_allowed(), false) then
        raise exception 'The media server must verify the uploaded files.' using errcode = 'KM015';
    end if;
    select * into v_clip from public.community_clips where id = p_clip_id for update;
    if v_clip.id is null or v_clip.owner_id <> v_actor or v_clip.storage_provider <> 'home' or v_clip.status = 'deleting' then
        raise exception 'Clip unavailable.' using errcode = 'KM007';
    end if;
    if v_clip.status = 'uploading' and v_clip.upload_expires_at <= clock_timestamp() then
        raise exception 'This upload expired. Start a new upload.' using errcode = 'KM017';
    end if;
    if not exists(select 1 from klipp_private.community_home_config where singleton and uploads_enabled) then
        raise exception 'Media uploads are temporarily unavailable.' using errcode = 'KM016';
    end if;
    if klipp_private.community_member_id(true) is null then raise exception 'Choose a username first.' using errcode = 'KM002'; end if;
    if p_video_bytes is distinct from v_clip.byte_size
      or not coalesce(p_poster_bytes between 1 and 262144, false)
      or not coalesce(p_duration_seconds > 0 and p_duration_seconds <= 60 and abs(p_duration_seconds - v_clip.duration_seconds) <= 0.5, false)
      or not coalesce(p_video_sha256 ~ '^[0-9a-f]{64}$' and p_poster_sha256 ~ '^[0-9a-f]{64}$', false) then
        raise exception 'Both validated files must match the reservation.' using errcode = 'KM006';
    end if;
    if v_clip.status = 'published' then
        if v_clip.video_sha256 is distinct from p_video_sha256 or v_clip.poster_sha256 is distinct from p_poster_sha256
          or v_clip.poster_byte_size is distinct from p_poster_bytes then
            raise exception 'Published media cannot be replaced.' using errcode = 'KM010';
        end if;
        return klipp_private.community_clip_json(p_clip_id, v_actor);
    end if;
    update public.community_clips set status = 'published', duration_seconds = p_duration_seconds,
        poster_byte_size = p_poster_bytes, video_sha256 = p_video_sha256, poster_sha256 = p_poster_sha256 where id = p_clip_id;
    return klipp_private.community_clip_json(p_clip_id, v_actor);
end;
$$;


create or replace function klipp_private.community_storage_access(p_bucket text, p_name text, p_operation text)
returns boolean language plpgsql security definer set search_path = '' as $$
declare v_actor uuid; v_clip public.community_clips%rowtype;
begin
    if p_bucket not in ('community-videos', 'community-posters') or p_operation = 'insert' then return false; end if;
    v_actor := klipp_private.community_member_id(false);
    if v_actor is null then return false; end if;
    select * into v_clip from public.community_clips c where
        (p_bucket = 'community-videos' and c.video_path = p_name)
        or (p_bucket = 'community-posters' and c.poster_path = p_name);
    if v_clip.id is null or v_clip.storage_provider <> 'supabase' then return false; end if;
    if p_operation = 'delete' then return v_clip.owner_id = v_actor and v_clip.status in ('uploading', 'deleting'); end if;
    if p_operation = 'select' then return (v_clip.owner_id = v_actor and v_clip.status in ('uploading','deleting')) or
        (v_clip.status = 'published' and not v_clip.moderation_hidden
        and not exists(select 1 from klipp_private.community_account_deletions where owner_id=v_clip.owner_id) and klipp_private.community_member_id(true) is not null
        and exists(select 1 from public.profiles where id = v_clip.owner_id and username is not null)
        and not klipp_private.community_blocked(v_actor, v_clip.owner_id)); end if;
    return false;
end;
$$;

commit;
