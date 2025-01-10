using System.Xml;
using Meshmakers.Octo.Services.Swagger.Configuration;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

namespace Meshmakers.Octo.Services.Swagger.Transformers;

internal class XmlDocOperationTransformer(IOptions<OctoOpenApiOptions> options) : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        if (!options.Value.XmlDocOperationAssemblies.Any())
        {
            return Task.CompletedTask;
        }

        foreach (var assembly in options.Value.XmlDocOperationAssemblies)
        {
            var xmlDocPath = assembly.Location.Replace(".dll", ".xml");
            if (!File.Exists(xmlDocPath))
            {
                throw new FileNotFoundException($"XML documentation file not found: {xmlDocPath}");
            }
            
            var xmlDoc = new XmlDocument();
            xmlDoc.Load(xmlDocPath);


            foreach (var descriptionGroup in context.DescriptionGroups)     
            {
                foreach (var apiDescription in descriptionGroup.Items)
                {
                    if (string.IsNullOrWhiteSpace(apiDescription.RelativePath) || string.IsNullOrWhiteSpace(apiDescription.HttpMethod))
                    {
                        continue;
                    }
                    
                    if (apiDescription.ActionDescriptor is ControllerActionDescriptor controllerActionDescriptor)
                    {
                        var xmlDocNodes = xmlDoc.SelectNodes($"//member[starts-with(@name, 'M:{controllerActionDescriptor.MethodInfo.DeclaringType?.FullName}.{controllerActionDescriptor.MethodInfo.Name}')]");
                        if (xmlDocNodes == null)
                        {
                            return Task.CompletedTask;
                        }

                        foreach (XmlNode node in xmlDocNodes)
                        {
                            if (document.Paths.TryGetValue("/" + apiDescription.RelativePath, out var pathItem))
                            {
                                if (Enum.TryParse<OperationType>(apiDescription.HttpMethod, true, out var operationType))
                                {
                                    var operation = pathItem.Operations
                                        .FirstOrDefault(o => o.Key == operationType);
                                    
                                    if (operation.Value == null)
                                    {
                                        continue;
                                    }
                                    
                                    SetOperationSummary(node, operation);
                                    
                                    foreach (var openApiParameter in operation.Value.Parameters)
                                    {
                                        var parameterNode = node.SelectSingleNode($"param[@name='{openApiParameter.Name}']");
                                        if (parameterNode == null)
                                        {
                                            continue;
                                        }
                                        openApiParameter.Description = parameterNode.InnerText.Trim();
                                    }
                                    
                                    
                                }
                            }
                        }
                    }
                }
            }

        }


        return Task.CompletedTask;
    }

    private static void SetOperationSummary(XmlNode node, KeyValuePair<OperationType, OpenApiOperation> operation)
    {
        var summaryNode = node.SelectSingleNode("summary");
        if (summaryNode == null)
        {
            return;
        }
        operation.Value.Summary = summaryNode.InnerText.Trim();
    }
}