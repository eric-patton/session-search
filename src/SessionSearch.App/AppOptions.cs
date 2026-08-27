using System.Globalization;
using SessionSearch.Infrastructure.Claude;

namespace SessionSearch.App;

internal sealed record AppOptions(
    string DataRoot,
    string ClaudeRoot,
    string CodexRoot,
    float UiScale,
    bool ForceHighContrast,
    bool ReducedMotion)
{
    public static AppOptions Parse(IReadOnlyList<string> arguments)
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        string dataRoot = Path.Combine(localAppData, "SessionSearch");
        string claudeRoot = ClaudeSessionProviderAdapter.GetConfiguredRootPath();
        string? codexEnvironment = Environment.GetEnvironmentVariable("CODEX_HOME");
        string codexRoot = string.IsNullOrWhiteSpace(codexEnvironment)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex")
            : codexEnvironment;
        float uiScale = 1F;
        bool forceHighContrast = false;
        bool reducedMotion = false;

        for (int index = 0; index < arguments.Count; index++)
        {
            string argument = arguments[index];
            if (string.Equals(argument, "--qa-high-contrast", StringComparison.Ordinal))
            {
                forceHighContrast = true;
                continue;
            }

            if (string.Equals(argument, "--qa-reduced-motion", StringComparison.Ordinal))
            {
                reducedMotion = true;
                continue;
            }

            if (index + 1 >= arguments.Count)
            {
                continue;
            }

            switch (argument)
            {
                case "--data-root":
                    dataRoot = arguments[++index];
                    break;
                case "--claude-root":
                    claudeRoot = arguments[++index];
                    break;
                case "--codex-root":
                    codexRoot = arguments[++index];
                    break;
                case "--qa-ui-scale" when float.TryParse(
                    arguments[index + 1],
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out float parsedScale):
                    index++;
                    uiScale = Math.Clamp(parsedScale, 1F, 2F);
                    break;
            }
        }

        return new AppOptions(
            dataRoot,
            claudeRoot,
            codexRoot,
            uiScale,
            forceHighContrast,
            reducedMotion);
    }
}
