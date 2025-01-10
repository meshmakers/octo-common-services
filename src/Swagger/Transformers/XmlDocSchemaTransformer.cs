using System.Xml;
using Meshmakers.Common.Shared;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

namespace Meshmakers.Octo.Services.Swagger.Transformers;

internal class XmlDocSchemaTransformer(IOptions<OctoOpenApiOptions> options) : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (!options.Value.DataTransferObjectAssemblies.Any())
        {
            return Task.CompletedTask;
        }

        if (!options.Value.DataTransferObjectAssemblies.Contains(context.JsonTypeInfo.Type.Assembly))
        {
            return Task.CompletedTask;
        }

        var xmlDocPath = options.Value.DataTransferObjectAssemblies.First().Location.Replace(".dll", ".xml");
        var xmlDoc = new XmlDocument();
        xmlDoc.Load(xmlDocPath);
        
        
        AddTypeDocumentation(xmlDoc, schema, context);
        AddPropertyDocumentation(xmlDoc, schema, context);


        return Task.CompletedTask;
    }
    
    private void AddTypeDocumentation(XmlDocument xmlDoc, OpenApiSchema schema, OpenApiSchemaTransformerContext context)
    {
        var fullTypeName = "'T:" + context.JsonTypeInfo.Type.FullName + "'";
        var xmlDocNodes = xmlDoc.SelectNodes($"//member[starts-with(@name, {fullTypeName})]");
        if (xmlDocNodes == null)
        {
            return;
        }

        foreach (XmlNode node in xmlDocNodes)
        {
            var summaryNode = node.SelectSingleNode("summary");
            if (summaryNode == null)
            {
                continue;
            }

            var summary = summaryNode.InnerText.Trim();
            schema.Description = summary;
        }
    }

    private void AddPropertyDocumentation(XmlDocument xmlDoc, OpenApiSchema schema, OpenApiSchemaTransformerContext context)
    {
        var fullTypeName = "'P:" + context.JsonTypeInfo.Type.FullName + "'";
        var xmlDocNodes = xmlDoc.SelectNodes($"//member[starts-with(@name, {fullTypeName})]");
        if (xmlDocNodes == null)
        {
            return;
        }

        foreach (XmlNode node in xmlDocNodes)
        {
            var name = node.Attributes?["name"]?.Value;
            var summaryNode = node.SelectSingleNode("summary");
            if (summaryNode == null || string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var summary = summaryNode.InnerText.Trim();
            var propertyName = name.Split('(')[0].Split('.').Last().ToCamelCase();

            if (schema.Properties.TryGetValue(propertyName, out var property))
            {
                property.Description = summary;
            }
        }
    }
}