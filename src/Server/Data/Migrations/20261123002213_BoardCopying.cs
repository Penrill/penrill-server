using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Penrill.Server.Data.Migrations
{
    /// <inheritdoc />
    public partial class BoardCopying : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Copying",
                table: "Boards",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "RemovedBoards",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    At = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RemovedBoards", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RemovedBoards");

            migrationBuilder.DropColumn(
                name: "Copying",
                table: "Boards");
        }
    }
}
