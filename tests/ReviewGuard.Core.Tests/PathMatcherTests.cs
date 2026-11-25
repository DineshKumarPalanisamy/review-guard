using ReviewGuard.Core;

namespace ReviewGuard.Core.Tests;

public sealed class PathMatcherTests
{
    [Theory]
    [InlineData("/src/App/Payments/Charge.cs", "**/Payments/**", true)]
    [InlineData("/Payments/Charge.cs", "**/Payments/**", true)]          // ** also means zero folders
    [InlineData("/src/App/payments/sub/Charge.cs", "**/Payments/**", true)] // case-insensitive
    [InlineData("/src/App/PaymentsUi/Charge.cs", "**/Payments/**", false)]
    [InlineData("/src/SampleApp/Auth/TokenService.cs", "src/SampleApp/Auth/**", true)]
    [InlineData("/other/src/SampleApp/Auth/TokenService.cs", "src/SampleApp/Auth/**", false)] // anchored at the root
    [InlineData("/src/App/appsettings.json", "**/appsettings*.json", true)]
    [InlineData("/src/App/appsettings.Production.json", "**/appsettings*.json", true)]
    [InlineData("/src/App/sub/x.json", "src/App/*.json", false)]          // * stays inside one folder
    [InlineData("/src/a.cs", "/src/?.cs", true)]
    public void Glob_patterns(string path, string pattern, bool expected)
    {
        Assert.Equal(expected, PathMatcher.IsMatch(path, pattern));
    }

    [Fact]
    public void Lists_add_up_default_project_and_repository()
    {
        var config = CriticalPathConfig.FromJson("""
            {
              // comments are allowed
              "default": ["**/Auth/**"],
              "projects": {
                "Internal": ["**/Migrations/**"],
                "Internal/sandbox": ["src/Billing/**", "**/Auth/**"]
              }
            }
            """);

        Assert.Equal(["**/Auth/**", "**/Migrations/**", "src/Billing/**"], config.PatternsFor("internal", "SANDBOX"));
        Assert.Equal(["**/Auth/**", "**/Migrations/**"], config.PatternsFor("Internal", "other"));
        Assert.Equal(["**/Auth/**"], config.PatternsFor("Other", "sandbox"));
        Assert.Empty(CriticalPathConfig.Empty.PatternsFor("Internal", "sandbox"));
    }

    [Fact]
    public void The_shipped_critical_paths_file_is_valid()
    {
        var file = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "ReviewGuard.Functions", "critical-paths.json");
        var config = CriticalPathConfig.FromJson(File.ReadAllText(file));

        var patterns = config.PatternsFor("Internal", "review-guard-sandbox");

        Assert.True(PathMatcher.Matching(["/src/SampleApp/Payments/PaymentService.cs"], patterns).Count == 1);
        Assert.Empty(PathMatcher.Matching(["/src/SampleApp/Catalog/ProductFormatter.cs"], patterns));
    }
}
