using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Curto.Api.Tests.Support;

/// <summary>A host environment with a chosen name, for unit tests that do not start a host.</summary>
/// <param name="name">Environment name, e.g. <c>Production</c>.</param>
internal sealed class FakeHostEnvironment(string name) : IHostEnvironment
{
    public string EnvironmentName { get; set; } = name;

    public string ApplicationName { get; set; } = "Curto.Api";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
