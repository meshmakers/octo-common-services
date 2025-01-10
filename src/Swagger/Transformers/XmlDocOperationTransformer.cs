using System.Xml;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

namespace Meshmakers.Octo.Services.Swagger.Transformers;

internal class XmlDocOperationTransformer(IOptions<OctoOpenApiOptions> options) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (!options.Value.OperationAssemblies.Any())
        {
            return Task.CompletedTask;
        }

        foreach (var assembly in options.Value.OperationAssemblies)
        {
            var xmlDocPath = assembly.Location.Replace(".dll", ".xml");
            if (!File.Exists(xmlDocPath))
            {
                throw new FileNotFoundException($"XML documentation file not found: {xmlDocPath}");
            }
            
            var xmlDoc = new XmlDocument();
            xmlDoc.Load(xmlDocPath);

            var xmlDocNodes = xmlDoc.SelectNodes("//member[starts-with(@name, 'M:')]");
            if (xmlDocNodes == null)
            {
                return Task.CompletedTask;
            }

            foreach (XmlNode node in xmlDocNodes)
            {
                var name = node.Attributes?["name"]?.Value;
                var summaryNode = node.SelectSingleNode("summary");
                if (summaryNode == null || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var summary = summaryNode.InnerText;
                var method = name.Split('(')[0].Split('.').Last();
                var controller = name.Split('(')[0].Split('.').Reverse().Skip(1).First();
                var controllerNode = document.Paths.Values.FirstOrDefault(p => p.Operations.Any(o => o.Value.OperationId == $"{controller}_{method}"));
                if (controllerNode == null)
                {
                    continue;
                }

                controllerNode.Description = summary;
            }
        }


        return Task.CompletedTask;
    }
}