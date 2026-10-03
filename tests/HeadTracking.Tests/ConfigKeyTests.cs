using System.Text.RegularExpressions;

namespace HeadTracking.Tests;

/// <summary>
/// BepInEx throws on these characters in a section or key name, and the throw happens inside the
/// plugin's Awake, where it is logged only to Player.log, so the plugin is silently inert.
/// Copied from SPerformanceT, which shipped an '=' in a key once. Reads every Config.Bind call in
/// the plugin's source.
/// </summary>
public class ConfigKeyTests
{
    private static readonly char[] Forbidden = { '=', '\n', '\t', '\\', '"', '\'', '[', ']' };

    private static readonly Regex Bind = new(
        @"\.Bind\(\s*""(?<section>[^""]*)""\s*,\s*""(?<key>(?:[^""\\]|\\.)*)""",
        RegexOptions.Compiled);

    private static string SourceRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "HeadTracking.Plugin")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!, "src", "HeadTracking.Plugin");
    }

    public static IEnumerable<object[]> Keys()
    {
        foreach (string file in Directory.GetFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar))
            {
                continue;
            }

            foreach (Match m in Bind.Matches(File.ReadAllText(file)))
            {
                yield return new object[] { Path.GetFileName(file), m.Groups["section"].Value, Regex.Unescape(m.Groups["key"].Value) };
            }
        }
    }

    [Fact]
    public void TheScanFindsTheBinds()
    {
        // Guards the test itself: if the regex stops matching, the theory below passes on nothing.
        Assert.True(Keys().Count() >= 2, "found only " + Keys().Count() + " Config.Bind calls");
    }

    [Theory]
    [MemberData(nameof(Keys))]
    public void ConfigNamesUseOnlyAllowedCharacters(string file, string section, string key)
    {
        Assert.False(string.IsNullOrEmpty(section), file + ": no section for '" + key + "'");
        Assert.True(section.IndexOfAny(Forbidden) < 0, file + ": section '" + section + "'");
        Assert.True(key.IndexOfAny(Forbidden) < 0, file + ": key '" + key + "'");
    }
}
