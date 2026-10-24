using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace KClock.Data.Migrations
{
    /// <summary>
    /// The settings downlink (DESIGN.md §5.2, "cfgv"). The table follows the isolation pattern
    /// of IsolationViewsAndGrants: RLS, never FORCE, owner-only writes that also require the
    /// binding to still be open (DESIGN.md §9: a former owner reads, never controls), and
    /// explicit grants. clock_ingest only ever reads it.
    /// </summary>
    public partial class DeviceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "cfgv",
                table: "device_state",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "device_settings",
                columns: table => new
                {
                    binding_id = table.Column<long>(type: "bigint", nullable: false),
                    dim_from = table.Column<short>(type: "smallint", nullable: false),
                    dim_until = table.Column<short>(type: "smallint", nullable: false),
                    marquee_minutes = table.Column<short>(type: "smallint", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_settings", x => x.binding_id);
                    table.CheckConstraint("ck_device_settings_dim_from", "dim_from BETWEEN 0 AND 24");
                    table.CheckConstraint("ck_device_settings_dim_until", "dim_until BETWEEN 0 AND 24");
                    table.CheckConstraint("ck_device_settings_marquee_minutes", "marquee_minutes BETWEEN 0 AND 60");
                    table.CheckConstraint("ck_device_settings_version", "version > 0");
                    table.ForeignKey(
                        name: "fk_device_settings_device_binding_binding_id",
                        column: x => x.binding_id,
                        principalTable: "device_binding",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                """
                CREATE SEQUENCE device_settings_version AS integer;

                ALTER TABLE device_settings ENABLE ROW LEVEL SECURITY;
                CREATE POLICY device_settings_select ON device_settings FOR SELECT TO clock_app
                    USING (EXISTS (
                        SELECT 1 FROM device_access da
                        WHERE da.binding_id = device_settings.binding_id
                          AND da.account_id = current_account_id()
                          AND da.revoked_at IS NULL
                    ));
                CREATE POLICY device_settings_insert_owner ON device_settings FOR INSERT TO clock_app
                    WITH CHECK (EXISTS (
                        SELECT 1 FROM device_access da
                        JOIN device_binding b ON b.id = da.binding_id
                        WHERE da.binding_id = device_settings.binding_id
                          AND da.account_id = current_account_id()
                          AND da.role = 'owner'
                          AND da.revoked_at IS NULL
                          AND b.unbound_at IS NULL
                    ));
                CREATE POLICY device_settings_update_owner ON device_settings FOR UPDATE TO clock_app
                    USING (EXISTS (
                        SELECT 1 FROM device_access da
                        JOIN device_binding b ON b.id = da.binding_id
                        WHERE da.binding_id = device_settings.binding_id
                          AND da.account_id = current_account_id()
                          AND da.role = 'owner'
                          AND da.revoked_at IS NULL
                          AND b.unbound_at IS NULL
                    ));
                CREATE POLICY device_settings_ingest_read ON device_settings FOR SELECT TO clock_ingest USING (true);

                GRANT SELECT, INSERT, UPDATE ON device_settings TO clock_app;
                GRANT USAGE ON SEQUENCE device_settings_version TO clock_app;
                GRANT SELECT ON device_settings TO clock_ingest;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The policies and grants go with the table; the sequence does not.
            migrationBuilder.Sql("DROP SEQUENCE IF EXISTS device_settings_version;");

            migrationBuilder.DropTable(
                name: "device_settings");

            migrationBuilder.DropColumn(
                name: "cfgv",
                table: "device_state");
        }
    }
}
