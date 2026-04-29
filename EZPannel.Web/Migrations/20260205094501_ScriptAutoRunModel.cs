using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EZPannel.Web.Migrations
{
    /// <inheritdoc />
    public partial class ScriptAutoRunModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
          

            migrationBuilder.CreateTable(
                name: "ScriptAutoRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    ScriptId = table.Column<int>(type: "INTEGER", nullable: false),
                    IsAutoRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    CronExpression = table.Column<string>(type: "TEXT", nullable: true),
                    ServerIds = table.Column<string>(type: "TEXT", nullable: false),
                    LastRunTime = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptAutoRuns", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
