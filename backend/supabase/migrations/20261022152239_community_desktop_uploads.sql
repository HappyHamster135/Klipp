-- Media on the web is for watching and interacting. Upload through Klipp on PC.
-- Browser-controlled Origin/Fetch Metadata headers cannot be removed by ordinary
-- browser JavaScript. This is a browser boundary, NOT proof that an arbitrary
-- HTTP client is the official app: custom clients can omit these headers.
-- Account/ownership checks, quotas and private Storage policies still apply.
begin;

create function klipp_private.community_upload_client_allowed()
returns boolean language sql stable security invoker set search_path = '' as $$
    select coalesce(jsonb_typeof(headers) = 'object'
        and not (headers ?| array['origin', 'sec-fetch-mode', 'sec-fetch-site', 'sec-fetch-dest']), false)
    from (select nullif(current_setting('request.headers', true), '')::jsonb headers) request
$$;
revoke all on function klipp_private.community_upload_client_allowed() from public, anon, authenticated;
grant execute on function klipp_private.community_upload_client_allowed() to authenticated;
comment on function klipp_private.community_upload_client_allowed() is
    'Blocks ordinary browser upload requests, including null Origin. Requires HTTP header context; does not attest a device or trust a client-supplied app marker.';

create or replace function public.community_begin_upload(p_title text, p_description text, p_duration_seconds double precision, p_byte_size bigint)
returns jsonb language plpgsql security invoker set search_path = '' as $$
begin
    if not klipp_private.community_upload_client_allowed() then
        raise exception 'Upload clips from Klipp on your PC.' using errcode = 'KM014';
    end if;
    return klipp_private.community_dispatch('begin', jsonb_build_object(
        'title', p_title, 'description', p_description, 'duration', p_duration_seconds, 'bytes', p_byte_size));
end;
$$;

create or replace function public.community_publish_clip(p_clip_id uuid)
returns jsonb language plpgsql security invoker set search_path = '' as $$
begin
    if not klipp_private.community_upload_client_allowed() then
        raise exception 'Upload clips from Klipp on your PC.' using errcode = 'KM014';
    end if;
    return klipp_private.community_dispatch('publish', jsonb_build_object('id', p_clip_id));
end;
$$;

-- Storage is a separate API, so guarding the reservation RPC alone is insufficient.
-- Restrictive ANDs with the existing reservation/owner checks even if a future
-- permissive policy grants upload access. Viewing and cleanup stay available.
create policy community_storage_browser_upload_guard on storage.objects
    as restrictive for insert to authenticated with check (
        bucket_id not in ('community-videos', 'community-posters')
        or (select klipp_private.community_upload_client_allowed()));

commit;
