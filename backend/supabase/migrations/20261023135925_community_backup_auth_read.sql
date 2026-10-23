-- Managed Auth's schema owner does not delegate USAGE's grant option to postgres.
-- Supabase's documented backup-reader route uses PostgreSQL's read-only preset.
-- This adds schema USAGE/SELECT throughout this project, never write privileges,
-- application-role membership, role delegation or permission to SET ROLE.
begin;
grant pg_read_all_data to klipp_backup_reader with inherit true;
grant pg_read_all_data to klipp_backup_reader with set false;
grant pg_read_all_data to klipp_backup_reader with admin false;
comment on role klipp_backup_reader is
    'Root-only logical backup reader. Read-all-data/BYPASSRLS for complete Auth recovery; no writes, app-role membership, delegation or SET ROLE. Never use in clients/API. Rotate on host compromise.';
do $$
begin
    if not has_schema_privilege('klipp_backup_reader','auth','USAGE')
      or not has_table_privilege('klipp_backup_reader','auth.users','SELECT') then
        raise exception 'The backup reader still lacks managed Auth read access.';
    end if;
    if exists(select 1 from pg_class c join pg_namespace n on n.oid=c.relnamespace
        where n.nspname in ('public','auth','klipp_private','storage') and c.relkind in ('r','p')
        and (has_table_privilege('klipp_backup_reader',c.oid,'INSERT')
          or has_table_privilege('klipp_backup_reader',c.oid,'UPDATE')
          or has_table_privilege('klipp_backup_reader',c.oid,'DELETE'))) then
        raise exception 'The backup reader must not have table write access.';
    end if;
    if exists(select 1 from pg_auth_members m join pg_roles r on r.oid=m.roleid
        where m.member='klipp_backup_reader'::regrole
        and (r.rolname <> 'pg_read_all_data' or m.admin_option or m.set_option or not m.inherit_option)) then
        raise exception 'Unexpected backup-reader membership options.';
    end if;
end;
$$;
commit;
