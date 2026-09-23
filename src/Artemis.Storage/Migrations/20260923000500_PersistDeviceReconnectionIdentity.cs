using Artemis.Storage;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Artemis.Storage.Migrations;

[DbContext(typeof(ArtemisDbContext))]
[Migration("20260923000500_PersistDeviceReconnectionIdentity")]
public partial class PersistDeviceReconnectionIdentity : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ReconnectionSignature",
            table: "Devices",
            type: "TEXT",
            maxLength: 4096,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "IdentifierAliases",
            table: "Devices",
            type: "TEXT",
            nullable: false,
            defaultValue: "[]");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(name: "ReconnectionSignature", table: "Devices");
        migrationBuilder.DropColumn(name: "IdentifierAliases", table: "Devices");
    }
}
