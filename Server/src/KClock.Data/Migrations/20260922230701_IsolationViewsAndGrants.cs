using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KClock.Data.Migrations
{
    /// <summary>
    /// Always last, per DESIGN.md §12: views, SECURITY DEFINER functions, RLS policies and
    /// grants. This is the migration that makes DESIGN.md §8's "design that survives either
    /// answer" real — RLS stays on the ordinary tables only (account, device, device_binding,
    /// device_access, enrollment_code, device_credential, plus device_state by the same
    /// reasoning as device even though DESIGN.md doesn't list it by name); telemetry and its
    /// hourly aggregate are never RLS-enabled and are reached only through security_barrier
    /// views owned by clock_migrator, so clock_app never holds a single grant on either
    /// hypertable-backed relation.
    ///
    /// One addition DESIGN.md's schema section doesn't spell out: find_or_create_account is a
    /// SECURITY DEFINER function for the Google sign-in callback, which by construction has no
    /// app.account_id GUC to scope by yet — that is precisely the case account's own RLS
    /// policy (id = current_account_id()) cannot satisfy for a brand-new account row. The
    /// function runs as its owner (clock_migrator, RLS-exempt) so clock_app can safely call it
    /// with zero direct grants on account/external_login otherwise.
    /// </summary>
    public partial class IsolationViewsAndGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // --- current_account_id(): the one place app.account_id is read back. STABLE, ---
            // --- never IMMUTABLE (§15 review item 4) — the GUC changes within a session.   ---
            // The NULLIF is load-bearing: missing_ok yields NULL only on a connection that has
            // never seen the GUC. Once a set_config(..., true) transaction has ended, the
            // pooled connection reports '' instead, and ''::uuid throws — an exception where
            // DESIGN.md §8 demands zero rows.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION current_account_id() RETURNS uuid
                LANGUAGE sql STABLE AS $$
                    SELECT NULLIF(current_setting('app.account_id', true), '')::uuid
                $$;
                """);

            // --- Account bootstrap: the one legitimate way to touch account/external_login ---
            // --- without an app.account_id GUC already set.                                ---
            migrationBuilder.Sql(
                """
                CREATE FUNCTION find_or_create_account(
                    p_provider text, p_subject text, p_email text, p_display_name text
                ) RETURNS uuid
                LANGUAGE plpgsql
                SECURITY DEFINER
                SET search_path = pg_catalog, public
                AS $$
                DECLARE
                    v_account_id uuid;
                BEGIN
                    SELECT account_id INTO v_account_id
                    FROM external_login
                    WHERE provider = p_provider AND subject = p_subject;

                    IF v_account_id IS NOT NULL THEN
                        RETURN v_account_id;
                    END IF;

                    INSERT INTO account (email, display_name)
                    VALUES (p_email, p_display_name)
                    RETURNING id INTO v_account_id;

                    INSERT INTO external_login (provider, subject, account_id)
                    VALUES (p_provider, p_subject, v_account_id);

                    RETURN v_account_id;
                END;
                $$;

                REVOKE ALL ON FUNCTION find_or_create_account(text, text, text, text) FROM PUBLIC;
                GRANT EXECUTE ON FUNCTION find_or_create_account(text, text, text, text) TO clock_app;
                """);

            // --- security_barrier views: the whole isolation mechanism for telemetry. ---
            // --- clock_app never receives a grant on telemetry or telemetry_1h themselves — ---
            // --- these views, owned by clock_migrator, are the only way through.            ---
            migrationBuilder.Sql(
                """
                CREATE VIEW telemetry_by_account WITH (security_barrier) AS
                SELECT t.*
                FROM telemetry t
                WHERE EXISTS (
                    SELECT 1 FROM device_access da
                    WHERE da.binding_id = t.binding_id
                      AND da.account_id = current_account_id()
                      AND da.revoked_at IS NULL
                );

                CREATE VIEW telemetry_1h_by_account WITH (security_barrier) AS
                SELECT h.*
                FROM telemetry_1h h
                WHERE EXISTS (
                    SELECT 1 FROM device_access da
                    WHERE da.binding_id = h.binding_id
                      AND da.account_id = current_account_id()
                      AND da.revoked_at IS NULL
                );

                GRANT SELECT ON telemetry_by_account TO clock_app;
                GRANT SELECT ON telemetry_1h_by_account TO clock_app;
                """);

            // --- RLS on the ordinary tables. FORCE is deliberately never set (§15 item — the ---
            // --- owner must stay exempt or migrations, and Timescale's background workers,  ---
            // --- break). Each table gets a clock_app policy scoped through device_access,    ---
            // --- and a broad PERMISSIVE clock_ingest policy — the ingest role legitimately   ---
            // --- crosses accounts to resolve a device's current binding/credential.          ---
            migrationBuilder.Sql(
                """
                ALTER TABLE account ENABLE ROW LEVEL SECURITY;
                CREATE POLICY account_self ON account FOR ALL TO clock_app
                    USING (id = current_account_id())
                    WITH CHECK (id = current_account_id());

                ALTER TABLE device ENABLE ROW LEVEL SECURITY;
                CREATE POLICY device_via_access ON device FOR SELECT TO clock_app
                    USING (EXISTS (
                        SELECT 1 FROM device_access da
                        JOIN device_binding db ON db.id = da.binding_id
                        WHERE db.device_id = device.id
                          AND da.account_id = current_account_id()
                          AND da.revoked_at IS NULL
                    ));
                CREATE POLICY device_ingest_all ON device FOR ALL TO clock_ingest USING (true) WITH CHECK (true);

                ALTER TABLE device_binding ENABLE ROW LEVEL SECURITY;
                CREATE POLICY device_binding_select ON device_binding FOR SELECT TO clock_app
                    USING (EXISTS (
                        SELECT 1 FROM device_access da
                        WHERE da.binding_id = device_binding.id
                          AND da.account_id = current_account_id()
                          AND da.revoked_at IS NULL
                    ));
                -- Control requires role = 'owner' on a still-open binding (DESIGN.md §9).
                CREATE POLICY device_binding_update_owner ON device_binding FOR UPDATE TO clock_app
                    USING (unbound_at IS NULL AND EXISTS (
                        SELECT 1 FROM device_access da
                        WHERE da.binding_id = device_binding.id
                          AND da.account_id = current_account_id()
                          AND da.role = 'owner'
                          AND da.revoked_at IS NULL
                    ))
                    WITH CHECK (EXISTS (
                        SELECT 1 FROM device_access da
                        WHERE da.binding_id = device_binding.id
                          AND da.account_id = current_account_id()
                          AND da.role = 'owner'
                          AND da.revoked_at IS NULL
                    ));
                CREATE POLICY device_binding_ingest_all ON device_binding FOR ALL TO clock_ingest USING (true) WITH CHECK (true);

                ALTER TABLE device_access ENABLE ROW LEVEL SECURITY;
                CREATE POLICY device_access_self ON device_access FOR SELECT TO clock_app
                    USING (account_id = current_account_id());
                CREATE POLICY device_access_ingest_all ON device_access FOR ALL TO clock_ingest USING (true) WITH CHECK (true);

                ALTER TABLE enrollment_code ENABLE ROW LEVEL SECURITY;
                CREATE POLICY enrollment_code_self ON enrollment_code FOR ALL TO clock_app
                    USING (account_id = current_account_id())
                    WITH CHECK (account_id = current_account_id());
                CREATE POLICY enrollment_code_ingest_all ON enrollment_code FOR ALL TO clock_ingest USING (true) WITH CHECK (true);

                ALTER TABLE device_credential ENABLE ROW LEVEL SECURITY;
                CREATE POLICY device_credential_select ON device_credential FOR SELECT TO clock_app
                    USING (EXISTS (
                        SELECT 1 FROM device_access da
                        WHERE da.binding_id = device_credential.binding_id
                          AND da.account_id = current_account_id()
                          AND da.revoked_at IS NULL
                    ));
                CREATE POLICY device_credential_ingest_all ON device_credential FOR ALL TO clock_ingest USING (true) WITH CHECK (true);

                ALTER TABLE device_state ENABLE ROW LEVEL SECURITY;
                CREATE POLICY device_state_via_access ON device_state FOR SELECT TO clock_app
                    USING (EXISTS (
                        SELECT 1 FROM device_access da
                        JOIN device_binding db ON db.id = da.binding_id
                        WHERE db.device_id = device_state.device_id
                          AND da.account_id = current_account_id()
                          AND da.revoked_at IS NULL
                    ));
                CREATE POLICY device_state_ingest_all ON device_state FOR ALL TO clock_ingest USING (true) WITH CHECK (true);
                """);

            // --- Explicit, table-by-table grants. Never ALTER DEFAULT PRIVILEGES (§15 item 7) ---
            // --- — that would silently grant clock_app read on telemetry the instant a       ---
            // --- future migration creates a new table.                                       ---
            migrationBuilder.Sql(
                """
                GRANT SELECT, UPDATE ON account TO clock_app;
                GRANT SELECT ON device TO clock_app;
                GRANT SELECT, UPDATE ON device_binding TO clock_app;
                GRANT SELECT ON device_access TO clock_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON enrollment_code TO clock_app;
                GRANT SELECT ON device_credential TO clock_app;
                GRANT SELECT ON device_state TO clock_app;

                GRANT SELECT, INSERT, UPDATE ON device TO clock_ingest;
                GRANT SELECT, INSERT, UPDATE ON device_binding TO clock_ingest;
                GRANT SELECT, INSERT ON device_access TO clock_ingest;
                GRANT SELECT, UPDATE ON enrollment_code TO clock_ingest;
                GRANT SELECT, INSERT, UPDATE ON device_credential TO clock_ingest;
                GRANT SELECT, INSERT, UPDATE ON device_state TO clock_ingest;
                -- SELECT as well as INSERT: TimescaleDB refuses INSERT ... ON CONFLICT on a
                -- hypertable without it ("permission denied for table telemetry"), and the
                -- ingest statement is ON CONFLICT DO NOTHING by design (DESIGN.md §5.4).
                -- clock_app still holds nothing on telemetry.
                GRANT SELECT, INSERT ON telemetry TO clock_ingest;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Views must go before InitialSchema's Down drops telemetry/telemetry_1h out from
            // under them. Policies and RLS-enablement are dropped automatically when their
            // owning tables are dropped next, so they are not repeated here.
            migrationBuilder.Sql("DROP VIEW IF EXISTS telemetry_1h_by_account;");
            migrationBuilder.Sql("DROP VIEW IF EXISTS telemetry_by_account;");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS find_or_create_account(text, text, text, text);");
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS current_account_id();");
        }
    }
}
