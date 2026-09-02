namespace MelodyBridge.Infrastructure.MediaServers;

/// <summary>User row of GET /Users, shared by the sync and the user picker.</summary>
public class JellyfinUserDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}
