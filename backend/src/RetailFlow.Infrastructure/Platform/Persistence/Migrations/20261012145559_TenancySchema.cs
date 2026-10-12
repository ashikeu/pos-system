using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RetailFlow.Infrastructure.Platform.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TenancySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddressLine",
                table: "Organization",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Organization",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContactEmail",
                table: "Organization",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NameBn",
                table: "Organization",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PhoneNumber",
                table: "Organization",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BranchSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PriceIncludesTax = table.Column<bool>(type: "bit", nullable: true),
                    AllowNegativeStock = table.Column<bool>(type: "bit", nullable: true),
                    RequireOpenShiftToSell = table.Column<bool>(type: "bit", nullable: true),
                    CashRoundingIncrement = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    CashRoundingMode = table.Column<byte>(type: "tinyint", nullable: true),
                    MaxLineDiscountPercent = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    VoidWindowMinutes = table.Column<int>(type: "int", nullable: true),
                    ReturnWindowDays = table.Column<int>(type: "int", nullable: true),
                    OfflineMaxDurationHours = table.Column<int>(type: "int", nullable: true),
                    OfflineMaxSaleAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    OfflineMaxCumulativeAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    LoyaltyEnabled = table.Column<bool>(type: "bit", nullable: true),
                    CreditSalesEnabled = table.Column<bool>(type: "bit", nullable: true),
                    ReceiptTemplate58 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReceiptTemplate80 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReceiptFooterText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConcurrencyToken = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BranchSettings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_BranchSettings_Branch_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PriceIncludesTax = table.Column<bool>(type: "bit", nullable: true),
                    AllowNegativeStock = table.Column<bool>(type: "bit", nullable: true),
                    RequireOpenShiftToSell = table.Column<bool>(type: "bit", nullable: true),
                    CashRoundingIncrement = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    CashRoundingMode = table.Column<byte>(type: "tinyint", nullable: true),
                    MaxLineDiscountPercent = table.Column<decimal>(type: "decimal(9,6)", precision: 9, scale: 6, nullable: true),
                    VoidWindowMinutes = table.Column<int>(type: "int", nullable: true),
                    ReturnWindowDays = table.Column<int>(type: "int", nullable: true),
                    OfflineMaxDurationHours = table.Column<int>(type: "int", nullable: true),
                    OfflineMaxSaleAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    OfflineMaxCumulativeAmount = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: true),
                    LoyaltyEnabled = table.Column<bool>(type: "bit", nullable: true),
                    CreditSalesEnabled = table.Column<bool>(type: "bit", nullable: true),
                    InvoiceNumberFormat = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReceiptTemplate58 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReceiptTemplate80 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReceiptFooterText = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConcurrencyToken = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationSettings", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_OrganizationSettings_Organization_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organization",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PosTerminal",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    DefaultWarehouseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ReceiptTemplate58 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    ReceiptTemplate80 = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    LastSyncAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    AppVersion = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    DeviceFingerprint = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    ConcurrencyToken = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PosTerminal", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_PosTerminal_Branch_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionPlan",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    MonthlyPrice = table.Column<decimal>(type: "decimal(19,4)", precision: 19, scale: 4, nullable: false),
                    CurrencyCode = table.Column<string>(type: "nchar(3)", fixedLength: true, maxLength: 3, nullable: false),
                    TrialDays = table.Column<int>(type: "int", nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ConcurrencyToken = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPlan", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                });

            migrationBuilder.CreateTable(
                name: "Warehouse",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BranchId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Type = table.Column<byte>(type: "tinyint", nullable: false),
                    IsDefault = table.Column<bool>(type: "bit", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false),
                    ConcurrencyToken = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Warehouse", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_Warehouse_Branch_BranchId",
                        column: x => x.BranchId,
                        principalTable: "Branch",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OrganizationSubscription",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Status = table.Column<byte>(type: "tinyint", nullable: false),
                    StartsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    CurrentPeriodEndsOn = table.Column<DateOnly>(type: "date", nullable: false),
                    TrialEndsOn = table.Column<DateOnly>(type: "date", nullable: true),
                    GraceDays = table.Column<int>(type: "int", nullable: false),
                    CancelledAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    CancellationReason = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ConcurrencyToken = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ChangeSequence = table.Column<long>(type: "bigint", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrganizationSubscription", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.ForeignKey(
                        name: "FK_OrganizationSubscription_Organization_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organization",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_OrganizationSubscription_SubscriptionPlan_PlanId",
                        column: x => x.PlanId,
                        principalTable: "SubscriptionPlan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PlanFeature",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Code = table.Column<byte>(type: "tinyint", nullable: false),
                    MaxCount = table.Column<int>(type: "int", nullable: true),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanFeature", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlanFeature_SubscriptionPlan_PlanId",
                        column: x => x.PlanId,
                        principalTable: "SubscriptionPlan",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BranchSettings_BranchId",
                table: "BranchSettings",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_BranchSettings_Org_ChangeSequence",
                table: "BranchSettings",
                columns: new[] { "OrganizationId", "ChangeSequence" });

            migrationBuilder.CreateIndex(
                name: "UX_BranchSettings_Org_Branch",
                table: "BranchSettings",
                columns: new[] { "OrganizationId", "BranchId" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_OrganizationSettings_Org",
                table: "OrganizationSettings",
                column: "OrganizationId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_OrganizationSubscription_PlanId",
                table: "OrganizationSubscription",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "UX_OrganizationSubscription_Org",
                table: "OrganizationSubscription",
                column: "OrganizationId",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_PlanFeature_Plan_Code",
                table: "PlanFeature",
                columns: new[] { "PlanId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PosTerminal_BranchId",
                table: "PosTerminal",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_PosTerminal_Org_Branch",
                table: "PosTerminal",
                columns: new[] { "OrganizationId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_PosTerminal_Org_ChangeSequence",
                table: "PosTerminal",
                columns: new[] { "OrganizationId", "ChangeSequence" });

            migrationBuilder.CreateIndex(
                name: "UX_PosTerminal_Org_Code",
                table: "PosTerminal",
                columns: new[] { "OrganizationId", "Code" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "UX_SubscriptionPlan_Code",
                table: "SubscriptionPlan",
                column: "Code",
                unique: true)
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_Warehouse_BranchId",
                table: "Warehouse",
                column: "BranchId");

            migrationBuilder.CreateIndex(
                name: "IX_Warehouse_Org_Branch",
                table: "Warehouse",
                columns: new[] { "OrganizationId", "BranchId" });

            migrationBuilder.CreateIndex(
                name: "IX_Warehouse_Org_ChangeSequence",
                table: "Warehouse",
                columns: new[] { "OrganizationId", "ChangeSequence" });

            migrationBuilder.CreateIndex(
                name: "UX_Warehouse_Branch_Default",
                table: "Warehouse",
                columns: new[] { "OrganizationId", "BranchId", "IsDefault" },
                unique: true,
                filter: "[IsDefault] = 1");

            migrationBuilder.CreateIndex(
                name: "UX_Warehouse_Org_Code",
                table: "Warehouse",
                columns: new[] { "OrganizationId", "Code" },
                unique: true)
                .Annotation("SqlServer:Clustered", true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BranchSettings");

            migrationBuilder.DropTable(
                name: "OrganizationSettings");

            migrationBuilder.DropTable(
                name: "OrganizationSubscription");

            migrationBuilder.DropTable(
                name: "PlanFeature");

            migrationBuilder.DropTable(
                name: "PosTerminal");

            migrationBuilder.DropTable(
                name: "Warehouse");

            migrationBuilder.DropTable(
                name: "SubscriptionPlan");

            migrationBuilder.DropColumn(
                name: "AddressLine",
                table: "Organization");

            migrationBuilder.DropColumn(
                name: "City",
                table: "Organization");

            migrationBuilder.DropColumn(
                name: "ContactEmail",
                table: "Organization");

            migrationBuilder.DropColumn(
                name: "NameBn",
                table: "Organization");

            migrationBuilder.DropColumn(
                name: "PhoneNumber",
                table: "Organization");
        }
    }
}
