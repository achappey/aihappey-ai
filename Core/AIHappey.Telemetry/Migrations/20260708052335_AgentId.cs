using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AIhappey.Telemetry.Migrations
{
    /// <inheritdoc />
    public partial class AgentId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AgentId",
                table: "Requests",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AgentId",
                table: "Requests");
        }
    }
}
