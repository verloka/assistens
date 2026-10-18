using System.Xml.Linq;

namespace PersonalAgent.UnitTests;

public sealed class ArchitectureTests
{
    [Fact]
    public void ProductionProjectsRespectModuleBoundariesAndHaveNoCycles()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PersonalAgent.sln")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        var projects = Directory.GetFiles(Path.Combine(directory.FullName, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar).Contains("obj"))
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path)!, path => path);

        Assert.Equal(7, projects.Count);
        var graph = projects.ToDictionary(pair => pair.Key, pair =>
            XDocument.Load(pair.Value).Descendants("ProjectReference")
                .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")!.Value.Replace('\\', '/')))
                .ToArray());

        Assert.Empty(graph["PersonalAgent.Core"]);
        foreach (var (name, references) in graph.Where(pair => pair.Key != "PersonalAgent.App" && pair.Key != "PersonalAgent.Core"))
            Assert.Equal(["PersonalAgent.Core"], references);

        Assert.Equal(projects.Keys.Where(name => name != "PersonalAgent.App").Order(), graph["PersonalAgent.App"].Order());
        foreach (var name in graph.Keys)
            Visit(name, []);

        void Visit(string name, HashSet<string> ancestors)
        {
            Assert.True(ancestors.Add(name), $"Circular reference through {name}");
            Assert.True(graph.ContainsKey(name), $"Unknown production project {name}");
            foreach (var reference in graph[name])
                Visit(reference, new HashSet<string>(ancestors));
        }
    }
}
