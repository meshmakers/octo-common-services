using System.Text;

namespace Meshmakers.Octo.Services.Methods.IntegrationTests.Adversarial;

/// <summary>
///     Records every case of the adversarial suite (AB#6915): case, test, observed code and reason, side effect. The rows
///     go to <c>adversarial-results.md</c> next to the test assembly, so a re-run produces the result table.
/// </summary>
internal static class AdversarialCaseLog
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "adversarial-results.md");
    private static bool _started;

    public static void Record(string caseId, string description, string test, string observed, string sideEffect)
    {
        lock (Gate)
        {
            var text = new StringBuilder();
            if (!_started)
            {
                _started = true;
                text.AppendLine("| Case | Attack | Test | Observed | Side effect |");
                text.AppendLine("|------|--------|------|----------|-------------|");
                File.WriteAllText(FilePath, string.Empty);
            }

            text.AppendLine($"| {caseId} | {description} | `{test}` | {observed} | {sideEffect} |");
            File.AppendAllText(FilePath, text.ToString());
        }
    }
}
