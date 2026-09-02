using Bunit;
using Microsoft.AspNetCore.Components;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// A NavigationManager pinned to a fixed URI, for pages that read Nav.Uri
/// during OnInitializedAsync (the OAuth callback page). bUnit's own manager
/// only changes its Uri through navigation, which happens after render.
/// </summary>
public sealed class FixedUriNavigationManager : NavigationManager
{
    public FixedUriNavigationManager(string baseUri, string uri)
        => Initialize(baseUri, uri);

    protected override void NavigateToCore(string to, bool forceLoad)
    {
        // The callback page never navigates during the exchange; ignore.
    }
}
