namespace MelodyBridge.Server.Components.Shared;

/// <summary>One playlist a batch account import saved or updated.</summary>
public sealed record ImportedPlaylistResult(string Id, string Name, int TrackCount, bool NewlySaved);
