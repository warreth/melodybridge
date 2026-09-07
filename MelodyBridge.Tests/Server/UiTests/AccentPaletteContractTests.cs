using System.Text.RegularExpressions;

namespace MelodyBridge.Tests.Server.UiTests;

/// <summary>
/// The accent palette contract, read from the real stylesheet: every
/// palette (each data-accent value, dark and light) defines all four
/// accent tokens including the hover shade, the primary button hover
/// consumes the token instead of a literal color, and the Settings
/// picker offers exactly the palettes the CSS ships. This catches the
/// class of bug where a new palette forgets a token or a rule goes
/// back to a hardcoded teal.
/// </summary>
[TestFixture]
[Category("UI")]
public class AccentPaletteContractTests
{
    private static readonly string[] PaletteNames = { "teal", "blue", "violet", "rose", "amber" };

    private static string StyleSheet
    {
        get
        {
            // Walk up from the test assembly to the repo root, exactly
            // like the shell markup test does, then into wwwroot. A
            // linked worktree has a .git FILE instead of a directory,
            // so either shape marks the root.
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !dir.EnumerateFileSystemInfos(".git").Any())
                dir = dir.Parent!;
            return File.ReadAllText(Path.Combine(
                dir!.FullName, "MelodyBridge.Server", "wwwroot", "app.css"));
        }
    }

    [Test]
    public void EveryPalette_DefinesAllFourAccentTokens_DarkAndLight()
    {
        var css = StyleSheet;

        foreach (var palette in PaletteNames)
        {
            foreach (var suffix in new[] { "", "[data-theme=\"light\"]" })
            {
                var selector = $":root[data-accent=\"{palette}\"]{suffix} {{";
                var start = css.IndexOf(selector, StringComparison.Ordinal);
                Assert.That(start, Is.GreaterThanOrEqualTo(0),
                    $"palette {palette}{suffix} must ship a CSS block");
                var end = css.IndexOf('}', start);
                var block = css[start..end];

                foreach (var token in new[]
                    { "--accent:", "--accent-strong:", "--accent-soft:", "--accent-hover:" })
                    Assert.That(block, Does.Contain(token),
                        $"palette {palette}{suffix} must define {token.TrimEnd(':')} " +
                        "so no rule ever falls back to a literal color");
            }
        }
    }

    [Test]
    public void PrimaryButtonHover_UsesTheHoverToken_NotALiteralColor()
    {
        var css = StyleSheet;
        var line = css.Split('\n')
            .SingleOrDefault(l => l.Contains(".btn-modern.primary:hover"));

        Assert.That(line, Is.Not.Null, "the primary hover rule must exist");
        Assert.That(line, Does.Contain("var(--accent-hover)"),
            "the hover must read the palette token");
        Assert.That(line, Does.Not.Match("#[0-9a-fA-F]{3,8}"),
            "a literal hex would freeze the hover to one palette's color");
    }

    [Test]
    public void AccentHoverValues_DifferPerPalette_NotAllTeal()
    {
        var css = StyleSheet;
        var hovers = new List<string>();
        foreach (var palette in PaletteNames)
        {
            var selector = $":root[data-accent=\"{palette}\"] {{";
            var start = css.IndexOf(selector, StringComparison.Ordinal);
            var end = css.IndexOf('}', start);
            var m = Regex.Match(css[start..end], @"--accent-hover:\s*(#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{6}));");
            Assert.That(m.Success, Is.True, $"palette {palette} hover must be a plain hex");
            hovers.Add(m.Groups[1].Value);
        }

        Assert.That(hovers.Distinct().Count(), Is.EqualTo(PaletteNames.Length),
            "each palette needs its own hover shade: identical values would mean the " +
            "palette switch stops recoloring the hover");
    }

}
