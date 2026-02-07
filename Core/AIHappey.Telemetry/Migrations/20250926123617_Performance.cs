using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIhappey.Telemetry.Migrations
{
    /// <inheritdoc />
    public partial class Performance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "CreatedAt",
                table: "Requests",
                newName: "StartedAt");

            migrationBuilder.AddColumn<DateTime>(
                name: "EndedAt",
                table: "Requests",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EndedAt",
                table: "Requests");

            migrationBuilder.RenameColumn(
                name: "StartedAt",
                table: "Requests",
                newName: "CreatedAt");
        }
    }
}
