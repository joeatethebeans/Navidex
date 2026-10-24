using System.Text.Json;

namespace Navidex.Core.Tests.Support;

/// <summary>The generated test library (tools/testlib) and its answer key.</summary>
public static class TestLibrary
{
    public static string RepositoryRoot { get; } = FindRoot();

    public static string Output => Path.Combine(RepositoryRoot, "tools", "testlib", "out");

    public static string Music => Path.Combine(Output, "music");

    public static string AnswerKey => Path.Combine(Output, "expected.json");

    public static string MusicBrainzFixtures => Path.Combine(RepositoryRoot, "tests", "fixtures", "musicbrainz");

    public static bool Exists => File.Exists(AnswerKey) && Directory.Exists(Music);

    public static JsonElement LoadAnswerKey() => JsonDocument.Parse(File.ReadAllText(AnswerKey)).RootElement;

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Navidex.sln")))
                return dir.FullName;
        throw new InvalidOperationException("Couldn't find the repository root above " + AppContext.BaseDirectory);
    }
}

/// <summary>A test that needs the generated library; skipped, with how to make it, when it hasn't been generated.</summary>
public sealed class TestLibraryFactAttribute : FactAttribute
{
    public TestLibraryFactAttribute()
    {
        if (!TestLibrary.Exists)
            Skip = "The test library hasn't been generated. Run: python3 tools/testlib/generate.py";
    }
}
