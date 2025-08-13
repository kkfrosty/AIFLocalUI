using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using AiFoundryUI.Models;

namespace AiFoundryUI.Services;

public class ThreadsRepository
{
    private readonly string _dbPath;

    public ThreadsRepository()
    {
        // Place portable DB next to the executable for zero-setup usage
        var baseDir = AppContext.BaseDirectory;
        _dbPath = Path.Combine(baseDir, "threads.db");
    }

    private SqliteConnection CreateConnection()
    {
        return new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
    }

    public async Task InitializeAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS Threads (
  Id TEXT PRIMARY KEY,
  Title TEXT NOT NULL,
  CreatedUtc TEXT NOT NULL,
  UpdatedUtc TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS Messages (
  Id INTEGER PRIMARY KEY AUTOINCREMENT,
  ThreadId TEXT NOT NULL,
  Role TEXT NOT NULL,
  Content TEXT NOT NULL,
  CreatedUtc TEXT NOT NULL,
  FOREIGN KEY(ThreadId) REFERENCES Threads(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS IX_Messages_ThreadId ON Messages(ThreadId);
";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<ChatThreadEntity>> GetThreadsAsync()
    {
        var result = new List<ChatThreadEntity>();
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, Title, CreatedUtc, UpdatedUtc FROM Threads ORDER BY datetime(UpdatedUtc) DESC";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new ChatThreadEntity
            {
                Id = Guid.Parse(reader.GetString(0)),
                Title = reader.GetString(1),
                CreatedUtc = DateTime.Parse(reader.GetString(2)),
                UpdatedUtc = DateTime.Parse(reader.GetString(3))
            });
        }
        return result;
    }

    public async Task<List<ChatMessageEntity>> GetMessagesAsync(Guid threadId)
    {
        var result = new List<ChatMessageEntity>();
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT Id, ThreadId, Role, Content, CreatedUtc FROM Messages WHERE ThreadId = $tid ORDER BY Id";
        cmd.Parameters.AddWithValue("$tid", threadId.ToString());
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new ChatMessageEntity
            {
                Id = reader.GetInt64(0),
                ThreadId = Guid.Parse(reader.GetString(1)),
                Role = reader.GetString(2),
                Content = reader.GetString(3),
                CreatedUtc = DateTime.Parse(reader.GetString(4))
            });
        }
        return result;
    }

    public async Task<Guid> CreateThreadAsync(string title)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Threads(Id, Title, CreatedUtc, UpdatedUtc) VALUES ($id, $title, $c, $u)";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$c", now.ToString("o"));
        cmd.Parameters.AddWithValue("$u", now.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
        return id;
    }

    public async Task UpdateThreadTitleAsync(Guid id, string title)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Threads SET Title = $title, UpdatedUtc = $u WHERE Id = $id";
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task TouchThreadAsync(Guid id)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE Threads SET UpdatedUtc = $u WHERE Id = $id";
        cmd.Parameters.AddWithValue("$u", DateTime.UtcNow.ToString("o"));
        cmd.Parameters.AddWithValue("$id", id.ToString());
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<long> AddMessageAsync(Guid threadId, string role, string content)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO Messages(ThreadId, Role, Content, CreatedUtc) VALUES ($tid, $role, $content, $c); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$tid", threadId.ToString());
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$c", DateTime.UtcNow.ToString("o"));
        var result = await cmd.ExecuteScalarAsync();
        // Touch thread on new message
        await TouchThreadAsync(threadId);
        return (result is long l) ? l : Convert.ToInt64(result);
    }
}
