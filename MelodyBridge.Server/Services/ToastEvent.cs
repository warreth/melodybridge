namespace MelodyBridge.Server.Services;

/// <summary>
/// One notification-worthy event: what happened, how bad it is and which
/// category it belongs to. The observer routes it to the toasts and to
/// ntfy, each with its own gating.
/// </summary>
public sealed record ToastEvent(string Category, string Message, string Level);
