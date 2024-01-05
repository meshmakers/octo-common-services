using Markdig;

namespace Meshmakers.Octo.Services.Infrastructure.Services;

public class MarkdownRenderService : IMarkdownRenderService
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownRenderService()
    {
        _pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
    }

    public string RenderPlainText(string markdown, Dictionary<string, Func<string>> replaceRules)
    {
        var temp = ExecuteReplaceRules(markdown, replaceRules);
        return temp;
    }

    public string RenderHtml(string markdown, Dictionary<string, Func<string>> replaceRules)
    {
        var temp = ExecuteReplaceRules(markdown, replaceRules);

        var bodyInHtml = Markdown.ToHtml(temp, _pipeline);

        var css = LoadCss("materialize.min.css");

        var html = @"
                <!DOCTYPE html PUBLIC ""-//W3C//DTD HTML 4.0 Transitional//EN"" ""http://www.w3.org/TR/REC-html40/loose.dtd"">
                <html>
                  <head>
                    <meta http-equiv=""Content-Type"" content=""text/html; charset=utf-8"">
                    <style>"
                   + css + @"
                    </style>
                  </head>
                  <body>
                   " + bodyInHtml + @"     
                  </body>
                </html>
            ";
        return html;
    }

    private static string ExecuteReplaceRules(string markdown, Dictionary<string, Func<string>> replaceRules)
    {
        var temp = markdown;
        foreach (var replaceRule in replaceRules)
        {
            temp = temp.Replace(replaceRule.Key, replaceRule.Value());
        }

        return temp;
    }

    private string LoadCss(string fileName)
    {
        var resourceName = $"Meshmakers.Octo.Backend.Infrastructure.Assets.{fileName}";

        using (var stream = typeof(MarkdownRenderService).Assembly.GetManifestResourceStream(resourceName))
        using (var reader =
               new StreamReader(stream ?? throw new KeyNotFoundException($"'{fileName}' not found in resources.")))
        {
            return reader.ReadToEnd();
        }
    }
}