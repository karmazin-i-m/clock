using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace KClock.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "account",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false, defaultValueSql: "gen_random_uuid()"),
                    email = table.Column<string>(type: "citext", nullable: true),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    deleted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "device",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    hardware_id = table.Column<string>(type: "text", nullable: false),
                    profile = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: true),
                    policy_sample_s = table.Column<int>(type: "integer", nullable: true),
                    policy_flush_s = table.Column<int>(type: "integer", nullable: true),
                    policy_max_batch = table.Column<int>(type: "integer", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "telemetry",
                columns: table => new
                {
                    device_id = table.Column<long>(type: "bigint", nullable: false),
                    ts = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    binding_id = table.Column<long>(type: "bigint", nullable: false),
                    temperature_dc = table.Column<short>(type: "smallint", nullable: true),
                    pressure_mmhg = table.Column<short>(type: "smallint", nullable: true),
                    humidity_pct = table.Column<short>(type: "smallint", nullable: true),
                    wifi_rssi_dbm = table.Column<short>(type: "smallint", nullable: true),
                    esp_free_heap_b = table.Column<int>(type: "integer", nullable: true),
                    esp_uptime_s = table.Column<int>(type: "integer", nullable: true),
                    link_good = table.Column<int>(type: "integer", nullable: true),
                    link_dropped = table.Column<int>(type: "integer", nullable: true),
                    link_rejected = table.Column<int>(type: "integer", nullable: true),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_telemetry", x => new { x.device_id, x.ts });
                    table.CheckConstraint("ck_telemetry_humidity_pct", "humidity_pct BETWEEN 0 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "enrollment_code",
                columns: table => new
                {
                    code_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    consumed_device_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_enrollment_code", x => x.code_hash);
                    table.ForeignKey(
                        name: "fk_enrollment_code_account_account_id",
                        column: x => x.account_id,
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "external_login",
                columns: table => new
                {
                    provider = table.Column<string>(type: "text", nullable: false),
                    subject = table.Column<string>(type: "text", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_external_login", x => new { x.provider, x.subject });
                    table.ForeignKey(
                        name: "fk_external_login_account_account_id",
                        column: x => x.account_id,
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_binding",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    device_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    timezone = table.Column<string>(type: "text", nullable: true),
                    bound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    unbound_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    unbound_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_binding", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_binding_account_account_id",
                        column: x => x.account_id,
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_device_binding_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_state",
                columns: table => new
                {
                    device_id = table.Column<long>(type: "bigint", nullable: false),
                    last_ingest_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_seq = table.Column<long>(type: "bigint", nullable: true),
                    esp_version = table.Column<string>(type: "text", nullable: true),
                    nano_version = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_state", x => x.device_id);
                    table.ForeignKey(
                        name: "fk_device_state_device_device_id",
                        column: x => x.device_id,
                        principalTable: "device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_access",
                columns: table => new
                {
                    binding_id = table.Column<long>(type: "bigint", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_access", x => new { x.binding_id, x.account_id });
                    table.ForeignKey(
                        name: "fk_device_access_account_account_id",
                        column: x => x.account_id,
                        principalTable: "account",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_access_device_bindings_binding_id",
                        column: x => x.binding_id,
                        principalTable: "device_binding",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "device_credential",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    device_id = table.Column<long>(type: "bigint", nullable: false),
                    binding_id = table.Column<long>(type: "bigint", nullable: false),
                    token_hash = table.Column<byte[]>(type: "bytea", nullable: false),
                    issued_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_reason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_credential", x => x.id);
                    table.ForeignKey(
                        name: "fk_device_credential_device_binding_binding_id",
                        column: x => x.binding_id,
                        principalTable: "device_binding",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_device_credential_device_device_id",
                        column: x => x.device_id,
                        principalTable: "device",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_device_hardware_id",
                table: "device",
                column: "hardware_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_access_account_id",
                table: "device_access",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_binding_account_id",
                table: "device_binding",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_binding_device_id",
                table: "device_binding",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_credential_binding_id",
                table: "device_credential",
                column: "binding_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_credential_device_id",
                table: "device_credential",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_credential_token_hash",
                table: "device_credential",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_enrollment_code_account_id",
                table: "enrollment_code",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_external_login_account_id",
                table: "external_login",
                column: "account_id");

            // --- Hand-owned SQL below: DESIGN.md §12's migration-ownership table puts
            // create_hypertable, compression and the partial unique index here, in the same
            // migration as the tables and before any row exists — converting an already
            // populated table to a hypertable takes heavy locks. §15's review checklist item 8
            // (compression delay > ring buffer + lateness budget) and item 9 (nullable, no
            // default on every future telemetry column) both apply from here on. ---

            // At most one open binding per device — this is what makes ownership a period
            // rather than a field enforceable, not just documented (DESIGN.md §7).
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX ix_device_binding_active ON device_binding (device_id) WHERE unbound_at IS NULL;
                """);

            // Must run before any row exists in telemetry, and the PK (device_id, ts) must
            // already exist (create_default_indexes: false — the PK covers ts already, a
            // second default index would be redundant).
            migrationBuilder.Sql(
                """
                SELECT create_hypertable('telemetry', 'ts', chunk_time_interval => INTERVAL '7 days', create_default_indexes => false);
                """);

            // compress_segmentby carries device_id AND binding_id — query pruning, GDPR erasure
            // touching only one binding's compressed batches, and correct per-owner aggregates
            // all depend on binding_id being in it (DESIGN.md §7). The 2 day delay must stay
            // above the device ring buffer (30-60 min) plus ingest_max_lateness (2 h) or a
            // replayed batch could land in an already-compressed chunk (DESIGN.md §7, §15 item 8).
            migrationBuilder.Sql(
                """
                ALTER TABLE telemetry SET (
                    timescaledb.compress,
                    timescaledb.compress_segmentby = 'device_id, binding_id',
                    timescaledb.compress_orderby   = 'ts DESC');
                SELECT add_compression_policy('telemetry', INTERVAL '2 days');
                """);

            // Retention exists and is deliberately disabled with an absurd interval, so turning
            // it on "to see what it does" does no harm (DESIGN.md §7).
            migrationBuilder.Sql(
                """
                DO $$ DECLARE j int;
                BEGIN
                  SELECT add_retention_policy('telemetry', INTERVAL '100 years') INTO j;
                  PERFORM alter_job(j, scheduled => false);
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Undo the hand-owned SQL before the generated DropTable calls remove telemetry —
            // DropTable alone would leave an orphaned job in timescaledb_information.jobs.
            migrationBuilder.Sql("SELECT remove_retention_policy('telemetry', if_exists => true);");
            migrationBuilder.Sql("SELECT remove_compression_policy('telemetry', if_exists => true);");

            migrationBuilder.DropTable(
                name: "device_access");

            migrationBuilder.DropTable(
                name: "device_credential");

            migrationBuilder.DropTable(
                name: "device_state");

            migrationBuilder.DropTable(
                name: "enrollment_code");

            migrationBuilder.DropTable(
                name: "external_login");

            migrationBuilder.DropTable(
                name: "telemetry");

            migrationBuilder.DropTable(
                name: "device_binding");

            migrationBuilder.DropTable(
                name: "account");

            migrationBuilder.DropTable(
                name: "device");
        }
    }
}
