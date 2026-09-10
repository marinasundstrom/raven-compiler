using System.Diagnostics;
using System.Text.RegularExpressions;

using Raven.CodeAnalysis.Syntax;
using Raven.CodeAnalysis.Testing;

namespace Raven.CodeAnalysis.Tests.CodeGen;

public sealed class AspNetCoreUnionInteropTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RavenContracts_RoundTripThroughMinimalApis(bool generatedDelegates)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"raven-http-unions-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sdk = RunDotnet(directory, "--version").Trim();
            Assert.StartsWith("11.", sdk);
            File.WriteAllText(Path.Combine(directory, "global.json"),
                System.Text.Json.JsonSerializer.Serialize(new { sdk = new { version = sdk, rollForward = "disable" } }));
            EmitModels(Path.Combine(directory, "Models.dll"));
            File.WriteAllText(Path.Combine(directory, "Host.csproj"), $$"""
                <Project Sdk="Microsoft.NET.Sdk.Web">
                  <PropertyGroup>
                    <TargetFramework>net11.0</TargetFramework>
                    <LangVersion>preview</LangVersion>
                    <Nullable>enable</Nullable>
                    <ImplicitUsings>enable</ImplicitUsings>
                    <EnableRequestDelegateGenerator>{{generatedDelegates.ToString().ToLowerInvariant()}}</EnableRequestDelegateGenerator>
                    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
                    <CompilerGeneratedFilesOutputPath>obj/generated</CompilerGeneratedFilesOutputPath>
                  </PropertyGroup>
                  <ItemGroup>
                    <Reference Include="Models"><HintPath>Models.dll</HintPath></Reference>
                  </ItemGroup>
                </Project>
                """);
            File.WriteAllText(Path.Combine(directory, "Program.cs"), HostSource);
            var build = RunDotnet(directory, "build", "/property:WarningLevel=0", "-v:minimal");
            Assert.DoesNotMatch(new Regex(@"\bRDG\d+\b"), build);
            if (generatedDelegates)
                Assert.Contains(Directory.GetFiles(Path.Combine(directory, "obj/generated"), "*.cs", SearchOption.AllDirectories),
                    path => path.Contains("RequestDelegateGenerator", StringComparison.Ordinal));
            RunDotnet(directory, "run", "--no-build", "--project", "Host.csproj");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void EmitModels(string path)
    {
        var references = TargetFrameworkResolver.GetReferenceAssemblies(TargetFrameworkResolver.ResolveVersion("net11.0"))
            .Where(File.Exists).Select(MetadataReference.CreateFromFile).ToArray();
        var tree = SyntaxTree.ParseText("""
            namespace Models
            public union Flag(bool | string)
            public union NumberOrText(int | string)
            public union Optional(int? | string)
            public record Cat(Name: string, Coat: string)
            public record Dog(Name: string, Breed: string)
            [System.Text.Json.Serialization.JsonUnion(TypeClassifier: typeof(System.Text.Json.Serialization.JsonUnionTypeStructuralClassifier))]
            public union Pet(Cat | Dog)
            public sealed record PaymentEvent(PaymentId: string) permits Authorized, Failed
            public record Authorized(PaymentId: string, Amount: decimal) : PaymentEvent(PaymentId)
            public record Failed(PaymentId: string, Reason: string) : PaymentEvent(PaymentId)
            """);
        var compilation = Compilation.Create("Models", [tree], references,
            new CompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var stream = File.Create(path);
        var emit = compilation.Emit(stream);
        Assert.True(emit.Success, string.Join(Environment.NewLine, emit.Diagnostics));
    }

    private static string RunDotnet(string directory, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = directory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            Assert.True(false, "dotnet process timed out: " + string.Join(" ", arguments));
        }
        var output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, output);
        return output;
    }

    private const string HostSource = """"
        using System.Net;
        using System.Text;
        using System.Text.Json;
        using Microsoft.AspNetCore.Hosting.Server;
        using Microsoft.AspNetCore.Hosting.Server.Features;
        using Models;

        var builder = WebApplication.CreateBuilder(args);
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.InferClosedTypePolymorphism = true);
        await using var app = builder.Build();
        app.MapPost("/flag", (Flag flag) => flag);
        app.MapGet("/number", () => Task.FromResult(new NumberOrText(42)));
        app.MapGet("/optional", () => new Optional((int?)null));
        app.MapPost("/pet", (Pet pet) => TypedResults.Ok(pet));
        app.MapPost("/event", (PaymentEvent payment) => TypedResults.Ok(payment));
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            await RoundTrip("/flag", "true");
            await RoundTrip("/flag", "\"hello\"");
            await RoundTrip("/pet", """{"name":"Milo","coat":"tabby"}""");
            await RoundTrip("/event", """{"$type":"Authorized","amount":42,"paymentId":"id"}""");
            if (await client.GetStringAsync("/number") != "42") throw new Exception("Async union response failed.");
            if (await client.GetStringAsync("/optional") != "null") throw new Exception("Nullable union response failed.");
            using var invalid = await client.PostAsync("/flag", new StringContent("{}", Encoding.UTF8, "application/json"));
            if (invalid.StatusCode != HttpStatusCode.BadRequest) throw new Exception("Malformed union input should return 400.");

            async Task RoundTrip(string route, string body)
            {
                using var response = await client.PostAsync(route, new StringContent(body, Encoding.UTF8, "application/json"));
                var actual = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode || !JsonElement.DeepEquals(JsonDocument.Parse(body).RootElement, JsonDocument.Parse(actual).RootElement))
                    throw new Exception($"{route}: {response.StatusCode}: {actual}");
            }
        }
        finally
        {
            await app.StopAsync();
        }
        """";
}
