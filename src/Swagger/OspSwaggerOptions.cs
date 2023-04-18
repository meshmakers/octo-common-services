using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Meshmakers.Octo.Backend.Swagger;

public class OctoSwaggerOptions
{
    public OctoSwaggerOptions()
    {
        XmlDocAssemblies = new List<string>();
    }

    public string? ApiTitle { get; set; }

    public string? ApiDescription { get; set; }

    public string? ClientId { get; set; }
    public string? AppName { get; set; }

    public ICollection<string> XmlDocAssemblies { get; }

    public string? AuthorityUrl { get; set; }

    public IDictionary<string, string> Scopes { get; set; } = new Dictionary<string, string>();

    public void AddXmlDocAssembly<T>()
    {
        XmlDocAssemblies.Add(GetAssemblyPath<T>());
    }

    private static string GetAssemblyPath<T>()
    {
        var codeBase = typeof(T).GetTypeInfo().Assembly.GetName().CodeBase;
        if (!string.IsNullOrWhiteSpace(codeBase))
        {
            return Path.ChangeExtension(new Uri(codeBase).LocalPath, ".xml");
        }

        throw new InvalidOperationException($"Assembly path of type '{typeof(T)}' not found.");
    }
}
