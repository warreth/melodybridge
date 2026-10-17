namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The raised-surface contract, read from the real stylesheet: the tokens
/// every settings card reads are defined in both themes, and the card
/// consumes them without a literal fallback. A missing light-theme value
/// would otherwise strip a card of its surface in silence, which is
/// exactly the class of bug the accent palette contract catches for the
/// accent tokens.
/// </summary>
[TestFixture]
[Category("UI")]
public class ThemeSurfaceContractTests
{
    private static readonly string[] RaisedTokens = { "--surface-raised", "--shadow-raised" };

    private static string StyleSheet
    {
        get
        {
            // Walk up from the test assembly to the repo root, exactly
            // like the accent palette contract does, then into wwwroot.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !dir.EnumerateFileSystemInfos(".git").Any())
                dir = dir.Parent!;
            return File.ReadAllText(Path.Combine(
                dir!.FullName, "MelodyBridge.Server", "wwwroot", "app.css"));
        }
    }

    /// <summary>The declaration block that starts at the given selector.</summary>
    private static string Block(string css, string selector)
    {
        var start = css.IndexOf(selector, StringComparison.Ordinal);
        Assert.That(start, Is.GreaterThanOrEqualTo(0), $"{selector} must ship a CSS block");
        var end = css.IndexOf('}', start);
        return css[start..end];
    }

    [Test]
    public void RaisedSurfaceTokens_AreDefinedInBothThemes()
    {
        var css = StyleSheet;
        var dark = Block(css, ":root {");
        var light = Block(css, ":root[data-theme=\"light\"] {");

        foreach (var token in RaisedTokens)
        {
            Assert.Multiple(() =>
            {
                Assert.That(dark, Does.Contain($"{token}:"),
                    $"the dark theme must define {token}, or every settings card loses its surface");
                Assert.That(light, Does.Contain($"{token}:"),
                    $"the light theme must define {token} as well");
            });
        }
    }

    [Test]
    public void SettingsCard_ConsumesTheRaisedTokens()
    {
        var css = StyleSheet;
        var card = Block(css, ".settings-card {");

        Assert.Multiple(() =>
        {
            Assert.That(card, Does.Contain("background: var(--surface-raised)"),
                "the settings surface must read the raised token");
            Assert.That(card, Does.Contain("box-shadow: var(--shadow-raised)"),
                "the lift must read the raised shadow token");
        });
    }

    [Test]
    public void RaisedTokens_AreNeverReadWithALiteralFallback()
    {
        var css = StyleSheet;

        foreach (var token in RaisedTokens)
            Assert.That(css, Does.Not.Contain($"var({token},"),
                $"a fallback would hide a missing {token} behind a literal color");
    }
}
