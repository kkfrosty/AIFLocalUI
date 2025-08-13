using System;

namespace AiFoundryUI.Models;

public class ChatThreadEntity
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "New Chat";
    public string? ThreadInstructions { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public class ChatMessageEntity
{
    public long Id { get; set; }
    public Guid ThreadId { get; set; }
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
