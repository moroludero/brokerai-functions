using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BrokerAi.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddPropertyCollageUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CollageUrl",
                table: "Properties",
                type: "nvarchar(max)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CollageUrl",
                table: "Properties");
        }
    }
}
