namespace Meshmakers.Octo.Services.Notifications.Services;

public interface IMarkdownRenderService
{
    string RenderPlainText(string markdown, Dictionary<string, Func<string>> replaceRules);

    string RenderHtml(string markdown, Dictionary<string, Func<string>> replaceRules);
}