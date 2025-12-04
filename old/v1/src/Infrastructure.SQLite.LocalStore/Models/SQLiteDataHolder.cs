using SQLite;

namespace Infrastructure.SQLite.LocalStore.Models;

public class SQLiteDataHolder
{
    [PrimaryKey]
    public string? Id { get; set; }

    public string? Group { get; set; }

    public string? Data { get; set; }
}
