using System.Diagnostics;
using System.Globalization;
using System.Text;
using SessionSearch.Core.Models;
using SessionSearch.Core.Search;
using SessionSearch.Core.Sessions;
using SessionSearch.Infrastructure.Claude;
using SessionSearch.Infrastructure.Codex;
using SessionSearch.Infrastructure.Indexing;
using SessionSearch.Infrastructure.Search;
using SessionSearch.Infrastructure.Storage;
using SessionSearch.Infrastructure.Windows;

namespace SessionSearch.App;

internal sealed class MainForm : Form
{
    private const string AllDirectoriesLabel = "All directories";
    private static readonly Color Frost = Color.FromArgb(244, 247, 246);
    private static readonly Color Paper = Color.FromArgb(253, 254, 253);
    private static readonly Color Ink = Color.FromArgb(23, 33, 40);
    private static readonly Color Smoke = Color.FromArgb(101, 116, 123);
    private static readonly Color Signal = Color.FromArgb(23, 107, 102);
    private static readonly Color Copper = Color.FromArgb(165, 92, 48);
    private static readonly Color Hairline = Color.FromArgb(213, 222, 220);
    private static readonly TrustedExecutableProfile ClaudeExecutableProfile = new(
        TrustedExecutableKind.ClaudeCode,
        "claude.exe",
        ["Anthropic, PBC"]);
    private static readonly TrustedExecutableProfile CodexExecutableProfile = new(
        TrustedExecutableKind.Codex,
        "codex.exe",
        ["OpenAI OpCo, LLC", "OpenAI, L.L.C."],
        new TrustedSignerPolicy(
            "codex-signer-v1",
            ["CN=\"OpenAI OpCo, LLC\", O=\"OpenAI OpCo, LLC\", L=San Francisco, S=California, C=US"]));
    private static readonly TrustedExecutableProfile TerminalExecutableProfile = new(
        TrustedExecutableKind.WindowsTerminal,
        "wt.exe",
        ["Microsoft Corporation"]);

    private readonly AppOptions options;
    private readonly AppPerformanceTelemetry telemetry = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim pageLoadGate = new(2, 2);
    private readonly System.Windows.Forms.Timer searchTimer = new() { Interval = 120 };
    private readonly System.Windows.Forms.Timer providerChangeTimer = new() { Interval = 1_500 };
    private readonly System.Windows.Forms.Timer periodicRescanTimer = new() { Interval = 300_000 };
    private readonly System.Windows.Forms.Timer idleMaintenanceTimer = new() { Interval = 5_000 };
    private readonly System.Windows.Forms.Timer availabilityRefreshTimer = new() { Interval = 180 };
    private readonly List<FileSystemWatcher> providerWatchers = [];
    private readonly TextBox searchBox = new();
    private readonly ComboBox scopeBox = new();
    private readonly Button indexStateLabel = new();
    private readonly Label resultCountLabel = new();
    private readonly Label statusLabel = new();
    private readonly ListBox favoriteDirectories = new();
    private readonly ListView resultList = new();
    private readonly SplitContainer favoritesSplit = new();
    private readonly SplitContainer detailsSplit = new();
    private readonly Label detailTitle = new();
    private readonly Label detailDescription = new();
    private readonly Label detailProvider = new();
    private readonly Label detailDirectory = new();
    private readonly TextBox detailIdentity = new();
    private readonly Label detailMatch = new();
    private readonly TextBox commandPreview = new();
    private readonly Button openButton = new();
    private readonly Button copyButton = new();
    private readonly Button sessionStarButton = new();
    private readonly Button directoryStarButton = new();
    private readonly Button rescanButton = new();
    private readonly TableLayoutPanel rootLayout = new();
    private readonly VirtualSessionResults virtualResults = new();

    private SessionDatabase? database;
    private SessionSearchService? searchService;
    private FavoritesRepository? favorites;
    private IndexingCoordinator? indexer;
    private LocalPathPolicy? pathPolicy;
    private SessionActionRouter? actionRouter;
    private ClaudeLiveActivityScanner? claudeActivityScanner;
    private CodexLiveActivityDiscovery? codexActivityDiscovery;
    private ResolvedExecutable? claudeExecutable;
    private ResolvedExecutable? codexExecutable;
    private ResolvedExecutable? windowsTerminal;
    private CancellationTokenSource? activeSearch;
    private Form? indexStatusForm;
    private TextBox? indexStatusText;
    private IndexingReport? lastIndexingReport;
    private Dictionary<SessionIdentity, AvailabilityDecision> currentAvailability = [];
    private Font? favoriteRowFont;
    private SearchGeneration? activeResultQuery;
    private Icon? applicationIcon;
    private int nextSearchGeneration;
    private int activeSearchCount;
    private bool indexingUiRunning;
    private bool rescanPending;
    private bool providerMonitoringStarted;
    private bool unmappedClaudeActivity;
    private bool reloadingFavoriteDirectories;
    private bool restoringResultState;
    private string? selectedFavoriteDirectory;
    private bool closing;

    public MainForm(AppOptions options)
    {
        this.options = options;
        Text = "Session Search";
        AccessibleName = "Session Search";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(960, 640);
        Size = new Size(1420, 860);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        BackColor = Frost;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        favoriteRowFont = new Font(Font, FontStyle.Bold);
        applicationIcon = LoadApplicationIcon();
        Icon = applicationIcon;

        BuildInterface();
        if (options.UiScale > 1F)
        {
            SuspendLayout();
            Scale(new SizeF(options.UiScale, options.UiScale));
            favoriteRowFont?.Dispose();
            favoriteRowFont = new Font(resultList.Font, FontStyle.Bold);
            MinimumSize = new Size(
                (int)Math.Ceiling(760 * options.UiScale),
                (int)Math.Ceiling(520 * options.UiScale));
            Size = new Size(
                (int)Math.Ceiling(1420 * options.UiScale),
                (int)Math.Ceiling(860 * options.UiScale));
            ResumeLayout(performLayout: true);
            NormalizeInjectedScaleLayout();
        }
        ApplySystemTheme();
        searchTimer.Tick += SearchTimerTick;
        providerChangeTimer.Tick += ProviderChangeTimerTick;
        periodicRescanTimer.Tick += PeriodicRescanTimerTick;
        idleMaintenanceTimer.Tick += IdleMaintenanceTimerTick;
        availabilityRefreshTimer.Tick += AvailabilityRefreshTimerTick;
        Shown += MainFormShown;
        FormClosing += MainFormClosing;
        KeyDown += MainFormKeyDown;
        Resize += MainFormResize;
    }

    private void BuildInterface()
    {
        rootLayout.Dock = DockStyle.Fill;
        rootLayout.ColumnCount = 1;
        rootLayout.RowCount = 3;
        rootLayout.Padding = new Padding(0);
        rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 102));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        Controls.Add(rootLayout);

        rootLayout.Controls.Add(BuildHeader(), 0, 0);
        rootLayout.Controls.Add(BuildWorkspace(), 0, 1);
        rootLayout.Controls.Add(BuildFooter(), 0, 2);
    }

    private Panel BuildHeader()
    {
        Panel header = new()
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 13, 20, 12),
            BackColor = Paper,
        };
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 2,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        header.Controls.Add(layout);

        Label brand = new()
        {
            Text = "SESSION INDEX  /  LOCAL",
            AutoSize = true,
            ForeColor = Signal,
            Font = new Font(Font.FontFamily, 9F, FontStyle.Bold),
            AccessibleName = "Session index, local only",
        };
        layout.Controls.Add(brand, 0, 0);

        indexStateLabel.Text = "Index: starting";
        indexStateLabel.TextAlign = ContentAlignment.MiddleRight;
        indexStateLabel.ForeColor = Smoke;
        indexStateLabel.Dock = DockStyle.Fill;
        indexStateLabel.Margin = new Padding(0);
        indexStateLabel.Padding = new Padding(0);
        indexStateLabel.AutoEllipsis = true;
        indexStateLabel.FlatStyle = FlatStyle.Flat;
        indexStateLabel.FlatAppearance.BorderSize = 0;
        indexStateLabel.BackColor = Paper;
        indexStateLabel.Cursor = Cursors.Hand;
        indexStateLabel.AccessibleDescription = "Open index health and diagnostics";
        indexStateLabel.Click += IndexStateLabelClick;
        layout.SetColumnSpan(indexStateLabel, 2);
        layout.Controls.Add(indexStateLabel, 1, 0);

        searchBox.Dock = DockStyle.Fill;
        searchBox.Font = new Font(Font.FontFamily, 14F, FontStyle.Regular);
        searchBox.PlaceholderText = "Search titles, paths, branches, models, and transcript text";
        searchBox.AccessibleName = "Search all indexed sessions";
        searchBox.Margin = new Padding(0, 2, 12, 0);
        searchBox.TextChanged += SearchBoxTextChanged;
        layout.Controls.Add(searchBox, 0, 1);

        scopeBox.Dock = DockStyle.Fill;
        scopeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        scopeBox.Items.AddRange(["All sessions", "Claude Code", "Codex", "Starred"]);
        scopeBox.SelectedIndex = 0;
        scopeBox.AccessibleName = "Provider and favorite scope";
        scopeBox.Margin = new Padding(0, 2, 12, 0);
        scopeBox.SelectedIndexChanged += ScopeBoxSelectedIndexChanged;
        layout.Controls.Add(scopeBox, 1, 1);

        rescanButton.Text = "Rescan";
        rescanButton.AccessibleName = "Rescan provider sessions";
        rescanButton.Dock = DockStyle.Fill;
        rescanButton.Margin = new Padding(0, 2, 0, 0);
        rescanButton.Click += RescanButtonClick;
        layout.Controls.Add(rescanButton, 2, 1);
        return header;
    }

    private SplitContainer BuildWorkspace()
    {
        favoritesSplit.Dock = DockStyle.Fill;
        favoritesSplit.FixedPanel = FixedPanel.Panel1;
        favoritesSplit.SplitterWidth = 1;
        favoritesSplit.BackColor = Hairline;
        favoritesSplit.SizeChanged += (_, _) => UpdateFavoritePaneLayout();
        favoritesSplit.Panel1.BackColor = Frost;
        favoritesSplit.Panel2.BackColor = Paper;
        favoritesSplit.Panel1.Padding = new Padding(16, 14, 12, 14);

        Label favoritesHeading = new()
        {
            Text = "FAVORITE DIRECTORIES",
            Dock = DockStyle.Top,
            Height = 26,
            ForeColor = Smoke,
            Font = new Font(Font.FontFamily, 8F, FontStyle.Bold),
        };
        favoriteDirectories.Dock = DockStyle.Fill;
        favoriteDirectories.BorderStyle = BorderStyle.None;
        favoriteDirectories.BackColor = Frost;
        favoriteDirectories.ForeColor = Ink;
        favoriteDirectories.AccessibleName = "Favorite directories";
        favoriteDirectories.SelectedIndexChanged += FavoriteDirectoriesSelectedIndexChanged;
        favoritesSplit.Panel1.Controls.Add(favoriteDirectories);
        favoritesSplit.Panel1.Controls.Add(favoritesHeading);

        detailsSplit.Dock = DockStyle.Fill;
        detailsSplit.FixedPanel = FixedPanel.None;
        detailsSplit.SplitterWidth = 1;
        detailsSplit.BackColor = Hairline;
        detailsSplit.SizeChanged += (_, _) => UpdateDetailPaneLayout();
        detailsSplit.Panel1.BackColor = Paper;
        detailsSplit.Panel2.BackColor = Frost;
        detailsSplit.Panel2.Padding = new Padding(18, 18, 18, 18);
        detailsSplit.Panel1.Controls.Add(BuildResultList());
        detailsSplit.Panel2.Controls.Add(BuildDetails());
        favoritesSplit.Panel2.Controls.Add(detailsSplit);
        return favoritesSplit;
    }

    private ListView BuildResultList()
    {
        resultList.Dock = DockStyle.Fill;
        resultList.BorderStyle = BorderStyle.None;
        resultList.View = View.Details;
        resultList.FullRowSelect = true;
        resultList.HideSelection = false;
        resultList.MultiSelect = true;
        resultList.VirtualMode = true;
        resultList.VirtualListSize = 0;
        resultList.AccessibleName = "Indexed session results";
        resultList.Columns.Add("Session / recent request", 300);
        resultList.Columns.Add("Provider", 85);
        resultList.Columns.Add("Directory / branch", 245);
        resultList.Columns.Add("Updated", 90);
        resultList.Columns.Add("State", 110);
        resultList.RetrieveVirtualItem += ResultListRetrieveVirtualItem;
        resultList.CacheVirtualItems += ResultListCacheVirtualItems;
        resultList.ItemSelectionChanged += ResultListItemSelectionChanged;
        resultList.DoubleClick += ResultListDoubleClick;
        return resultList;
    }

    private TableLayoutPanel BuildDetails()
    {
        TableLayoutPanel details = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 10,
        };
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 150));
        details.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        details.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));

        Label heading = new()
        {
            Text = "SESSION DETAIL",
            Dock = DockStyle.Fill,
            ForeColor = Smoke,
            Font = new Font(Font.FontFamily, 8F, FontStyle.Bold),
        };
        details.Controls.Add(heading, 0, 0);

        ConfigureDetailLabel(detailTitle, 15F, FontStyle.Bold, Ink);
        detailTitle.AccessibleDescription = "Selected session title";
        details.Controls.Add(detailTitle, 0, 1);
        ConfigureDetailLabel(detailDescription, 9F, FontStyle.Regular, Smoke);
        detailDescription.AccessibleDescription = "Selected session description";
        details.Controls.Add(detailDescription, 0, 2);
        ConfigureDetailLabel(detailProvider, 9F, FontStyle.Bold, Signal);
        detailProvider.AccessibleDescription = "Selected session provider and state";
        details.Controls.Add(detailProvider, 0, 3);
        ConfigureDetailLabel(detailDirectory, 9F, FontStyle.Regular, Ink);
        detailDirectory.AccessibleDescription = "Selected session directory";
        details.Controls.Add(detailDirectory, 0, 4);
        detailIdentity.ReadOnly = true;
        detailIdentity.Multiline = true;
        detailIdentity.WordWrap = false;
        detailIdentity.ScrollBars = ScrollBars.Horizontal;
        detailIdentity.BorderStyle = BorderStyle.None;
        detailIdentity.TabStop = false;
        detailIdentity.Dock = DockStyle.Fill;
        detailIdentity.BackColor = Frost;
        detailIdentity.ForeColor = Smoke;
        detailIdentity.Font = new Font("Consolas", 8F, FontStyle.Regular, GraphicsUnit.Point);
        detailIdentity.AccessibleDescription = "Selected session identifier";
        details.Controls.Add(detailIdentity, 0, 5);
        ConfigureDetailLabel(detailMatch, 9F, FontStyle.Italic, Copper);
        detailMatch.AccessibleDescription = "Selected session match excerpt";
        details.Controls.Add(detailMatch, 0, 6);

        commandPreview.ReadOnly = true;
        commandPreview.Multiline = true;
        commandPreview.WordWrap = false;
        commandPreview.ScrollBars = ScrollBars.Horizontal;
        commandPreview.Dock = DockStyle.Fill;
        commandPreview.Font = new Font("Consolas", 8.5F, FontStyle.Regular, GraphicsUnit.Point);
        commandPreview.BackColor = Paper;
        commandPreview.ForeColor = Ink;
        commandPreview.AccessibleName = "PowerShell resume command preview";
        details.Controls.Add(commandPreview, 0, 7);

        sessionStarButton.Text = "Star session";
        sessionStarButton.AccessibleName = "Toggle selected session favorite";
        sessionStarButton.Dock = DockStyle.Fill;
        sessionStarButton.Click += SessionStarButtonClick;
        details.Controls.Add(sessionStarButton, 0, 8);

        directoryStarButton.Text = "Star directory";
        directoryStarButton.AccessibleName = "Toggle selected directory favorite";
        directoryStarButton.Dock = DockStyle.Fill;
        directoryStarButton.Click += DirectoryStarButtonClick;
        details.Controls.Add(directoryStarButton, 0, 9);
        ShowEmptyDetails();
        return details;
    }

    private Panel BuildFooter()
    {
        Panel footer = new()
        {
            Dock = DockStyle.Fill,
            BackColor = Ink,
            Padding = new Padding(16, 6, 12, 6),
        };
        resultCountLabel.AutoSize = false;
        resultCountLabel.Width = 170;
        resultCountLabel.Dock = DockStyle.Left;
        resultCountLabel.TextAlign = ContentAlignment.MiddleLeft;
        resultCountLabel.ForeColor = Color.White;

        statusLabel.Dock = DockStyle.Fill;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLabel.ForeColor = Color.FromArgb(208, 219, 217);
        statusLabel.AccessibleDescription = "Application status messages";
        statusLabel.AccessibleRole = AccessibleRole.StatusBar;
        statusLabel.LiveSetting = System.Windows.Forms.Automation.AutomationLiveSetting.Polite;
        statusLabel.Text = "Starting local index";

        copyButton.Text = "Copy";
        copyButton.Width = 132;
        copyButton.Dock = DockStyle.Right;
        copyButton.Enabled = false;
        StyleFooterButton(copyButton);
        copyButton.AccessibleName = "Copy resume command for selected session";
        copyButton.Click += CopyButtonClick;

        openButton.Text = "Open";
        openButton.Width = 142;
        openButton.Dock = DockStyle.Right;
        openButton.Enabled = false;
        StyleFooterButton(openButton);
        openButton.AccessibleName = "Open selected session in Windows Terminal";
        openButton.Click += OpenButtonClick;

        footer.Controls.Add(statusLabel);
        footer.Controls.Add(resultCountLabel);
        footer.Controls.Add(copyButton);
        footer.Controls.Add(openButton);
        return footer;
    }

    private static void StyleFooterButton(Button button)
    {
        button.BackColor = Paper;
        button.ForeColor = Ink;
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Hairline;
        button.UseVisualStyleBackColor = false;
    }

    private static void ConfigureDetailLabel(
        Label label,
        float size,
        FontStyle style,
        Color color)
    {
        label.Dock = DockStyle.Fill;
        label.AutoEllipsis = true;
        label.ForeColor = color;
        label.Font = new Font("Segoe UI", size, style, GraphicsUnit.Point);
    }

    private void ApplySystemTheme()
    {
        if (!options.ForceHighContrast && !SystemInformation.HighContrast)
        {
            return;
        }

        BackColor = SystemColors.Window;
        ForeColor = SystemColors.WindowText;
        ApplySystemColors(this);
    }

    private static void ApplySystemColors(Control control)
    {
        control.BackColor = control is Button
            ? SystemColors.Control
            : SystemColors.Window;
        control.ForeColor = control is Button
            ? SystemColors.ControlText
            : SystemColors.WindowText;
        foreach (Control child in control.Controls)
        {
            ApplySystemColors(child);
        }
    }

    private async void IndexStateLabelClick(object? sender, EventArgs e)
    {
        if (indexStatusForm is not null)
        {
            indexStatusForm.Activate();
            return;
        }

        Form statusForm = new()
        {
            Text = "Index status",
            AccessibleName = "Index status",
            StartPosition = FormStartPosition.CenterParent,
            Size = new Size(780, 640),
            MinimumSize = new Size(620, 440),
            ShowInTaskbar = false,
            KeyPreview = true,
            BackColor = BackColor,
            ForeColor = ForeColor,
            Font = Font,
        };
        TableLayoutPanel layout = new()
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(12),
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        TextBox details = new()
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            WordWrap = false,
            ScrollBars = ScrollBars.Both,
            AccessibleName = "Index health and diagnostics",
            Text = "Loading index status...",
        };
        FlowLayoutPanel actions = new()
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 8, 0, 0),
        };
        Button closeButton = new()
        {
            Text = "Close",
            AutoSize = true,
            AccessibleName = "Close index status",
        };
        Button statusRescanButton = new()
        {
            Text = "Rescan",
            AutoSize = true,
            AccessibleName = "Rescan session sources",
        };
        closeButton.Click += (_, _) => statusForm.Close();
        statusRescanButton.Click += async (_, _) =>
        {
            await RunIndexingAsync();
            await RefreshIndexStatusAsync();
        };
        actions.Controls.Add(closeButton);
        actions.Controls.Add(statusRescanButton);
        layout.Controls.Add(details, 0, 0);
        layout.Controls.Add(actions, 0, 1);
        statusForm.Controls.Add(layout);
        statusForm.KeyDown += (_, keyEvent) =>
        {
            if (keyEvent.KeyCode == Keys.Escape)
            {
                statusForm.Close();
                keyEvent.SuppressKeyPress = true;
            }
        };
        statusForm.FormClosed += (_, _) =>
        {
            indexStatusForm = null;
            indexStatusText = null;
            if (!closing)
            {
                indexStateLabel.Focus();
            }
        };

        indexStatusForm = statusForm;
        indexStatusText = details;
        ApplySystemColors(statusForm);
        statusForm.Show(this);
        await RefreshIndexStatusAsync();
    }

    private async Task RefreshIndexStatusAsync()
    {
        if (indexStatusText is null || database is null)
        {
            return;
        }

        IReadOnlyList<PersistedProviderDiagnostic> diagnostics;
        try
        {
            diagnostics = await new DiagnosticsRepository(database).ListRecentAsync(
                20,
                lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            indexStatusText.Text = telemetry.FormatStatus(database.DatabasePath)
                + Environment.NewLine
                + Environment.NewLine
                + $"Diagnostics unavailable: {SafeError(exception)}";
            return;
        }

        if (indexStatusText is null)
        {
            return;
        }

        StringBuilder status = new();
        status.AppendLine(telemetry.FormatStatus(database.DatabasePath));
        status.AppendLine();
        status.AppendLine("Provider roots");
        status.AppendLine(
            CultureInfo.CurrentCulture,
            $"Claude Code: {RootStatus(options.ClaudeRoot)}");
        status.AppendLine(
            CultureInfo.CurrentCulture,
            $"Codex: {RootStatus(options.CodexRoot)}");
        status.AppendLine();
        status.AppendLine("Latest index pass");
        if (lastIndexingReport is null)
        {
            status.AppendLine("No completed pass in this process yet.");
        }
        else
        {
            status.AppendLine(
                CultureInfo.CurrentCulture,
                $"Sessions: {lastIndexingReport.CompletedSessions:N0}/{lastIndexingReport.DiscoveredSessions:N0}");
            status.AppendLine(
                CultureInfo.CurrentCulture,
                $"Changed sources: {lastIndexingReport.ChangedSources:N0}");
            status.AppendLine(
                CultureInfo.CurrentCulture,
                $"Processed: {FormatBytes(lastIndexingReport.ProcessedBytes)}");
            status.AppendLine(
                CultureInfo.CurrentCulture,
                $"Elapsed: {lastIndexingReport.Elapsed.TotalSeconds:0.0} seconds");
            status.AppendLine(
                CultureInfo.CurrentCulture,
                $"State: {(lastIndexingReport.IsPartial ? "Partial" : "Ready")}");
        }

        status.AppendLine();
        status.AppendLine("Recent sanitized diagnostics");
        if (diagnostics.Count == 0)
        {
            status.AppendLine("None.");
        }
        else
        {
            foreach (PersistedProviderDiagnostic diagnostic in diagnostics)
            {
                string provider = diagnostic.Provider?.ToString() ?? "App";
                status.AppendLine(
                    CultureInfo.CurrentCulture,
                    $"[{diagnostic.Severity}] {provider} {diagnostic.Code} ({diagnostic.SourceAlias})");
                status.Append("  ").Append(diagnostic.Message);
                if (diagnostic.ParserVersion.HasValue)
                {
                    status.Append(
                        CultureInfo.CurrentCulture,
                        $" Parser {diagnostic.ParserVersion.Value}.");
                }

                if (diagnostic.RetryState != 0)
                {
                    status.Append(
                        CultureInfo.CurrentCulture,
                        $" Retry state {diagnostic.RetryState}.");
                }

                if (diagnostic.ElapsedMilliseconds.HasValue)
                {
                    status.Append(
                        CultureInfo.CurrentCulture,
                        $" {diagnostic.ElapsedMilliseconds.Value} ms.");
                }

                if (diagnostic.ExceptionType is not null)
                {
                    status.Append(
                        CultureInfo.CurrentCulture,
                        $" {diagnostic.ExceptionType}.");
                }

                status.AppendLine();
            }
        }

        indexStatusText.Text = status.ToString();
        indexStatusText.SelectionStart = 0;
        indexStatusText.SelectionLength = 0;
    }

    private static string RootStatus(string path) =>
        Directory.Exists(path) ? $"Available, {path}" : $"Unavailable, {path}";

    private void UpdateUnmappedClaudeWarning()
    {
        const string suffix = " | Claude activity unmapped";
        string baseText = indexStateLabel.Text.Replace(
            suffix,
            string.Empty,
            StringComparison.Ordinal);
        indexStateLabel.Text = unmappedClaudeActivity
            ? baseText + suffix
            : baseText;
    }

    private void MainFormShown(object? sender, EventArgs e)
    {
        NormalizeInjectedScaleLayout();
        _ = InitializeAsync();
    }

    private void MainFormResize(object? sender, EventArgs e) =>
        NormalizeInjectedScaleLayout();

    private void NormalizeInjectedScaleLayout()
    {
        if (options.UiScale <= 1F || rootLayout.IsDisposed)
        {
            return;
        }

        rootLayout.Dock = DockStyle.None;
        rootLayout.MinimumSize = Size.Empty;
        rootLayout.MaximumSize = Size.Empty;
        rootLayout.Location = Point.Empty;
        rootLayout.Size = ClientSize;
        favoritesSplit.MinimumSize = Size.Empty;
        favoritesSplit.MaximumSize = Size.Empty;
        detailsSplit.MinimumSize = Size.Empty;
        detailsSplit.MaximumSize = Size.Empty;
        rootLayout.PerformLayout();
        favoritesSplit.PerformLayout();
        detailsSplit.PerformLayout();
        UpdateFavoritePaneLayout();
        UpdateDetailPaneLayout();
    }

    private void UpdateFavoritePaneLayout()
    {
        if (favoritesSplit.IsDisposed || favoritesSplit.ClientSize.Width <= 0)
        {
            return;
        }

        float logicalWidth = favoritesSplit.ClientSize.Width / options.UiScale;
        bool collapseFavorites = logicalWidth < 900F;
        if (favoritesSplit.Panel1Collapsed != collapseFavorites)
        {
            favoritesSplit.Panel1Collapsed = collapseFavorites;
        }

        int desiredDistance = (int)Math.Ceiling(220 * options.UiScale);
        if (!collapseFavorites && favoritesSplit.SplitterDistance != desiredDistance)
        {
            favoritesSplit.SplitterDistance = desiredDistance;
        }
    }

    private void UpdateDetailPaneLayout()
    {
        if (detailsSplit.IsDisposed || detailsSplit.ClientSize.Width <= 0)
        {
            return;
        }

        float logicalWidth = detailsSplit.ClientSize.Width / options.UiScale;
        bool collapseDetails = logicalWidth <= 760F;
        if (detailsSplit.Panel2Collapsed != collapseDetails)
        {
            detailsSplit.Panel2Collapsed = collapseDetails;
        }

        if (!collapseDetails)
        {
            int desiredDistance = detailsSplit.ClientSize.Width -
                (int)Math.Ceiling(360 * options.UiScale);
            if (Math.Abs(detailsSplit.SplitterDistance - desiredDistance) > 2)
            {
                detailsSplit.SplitterDistance = desiredDistance;
            }
        }
    }

    private async Task InitializeAsync()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        Task<ActionComposition> actionCompositionTask = Task.Run(
            CreateActionComposition,
            lifetime.Token);
        try
        {
            string databasePath = Path.Combine(options.DataRoot, "session-search.sqlite3");
            database = await SessionDatabase.CreateAsync(
                databasePath,
                protectDirectory: true,
                lifetime.Token);
            favorites = new FavoritesRepository(database);
            indexer = new IndexingCoordinator(
                database,
                [
                    new ProviderRegistration(
                        new ClaudeSessionProviderAdapter(),
                        options.ClaudeRoot),
                    new ProviderRegistration(
                        new CodexProviderAdapter(),
                        options.CodexRoot),
                ]);
            searchService = new SessionSearchService(database, () => indexer.IsPartial);

            await ReloadFavoriteDirectoriesAsync(lifetime.Token);
            await RunSearchAsync(resetPage: true, lifetime.Token);
            telemetry.RecordFirstUsableRows();
            indexStateLabel.Text = $"Index: cached rows in {stopwatch.ElapsedMilliseconds} ms";
            statusLabel.Text = "Cached sessions are ready. Checking provider files in the background.";

            try
            {
                ApplyActionComposition(await actionCompositionTask);
                await RefreshAvailabilityAsync(lifetime.Token);
                statusLabel.Text = windowsTerminal is null
                    ? "Sessions are ready. Windows Terminal was not found, so use Copy command."
                    : "Sessions and resume actions are ready.";
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                statusLabel.Text = $"Resume actions are unavailable: {SafeError(exception)}";
            }

            await RunIndexingAsync();
            StartProviderMonitoring();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            indexStateLabel.Text = "Index: unavailable";
            statusLabel.Text = $"Startup failed: {SafeError(exception)}";
        }
    }

    private static ActionComposition CreateActionComposition()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var localPathPolicy = new LocalPathPolicy(new PhysicalWindowsPathProbe());
        var executableResolver = new TrustedExecutableResolver(
            localPathPolicy,
            new AuthenticodeExecutableTrustVerifier(),
            new CodexInstallerAliasPolicy(
                localPathPolicy,
                new PhysicalDirectoryRedirectReader(),
                userProfile,
                localAppData));
        ResolvedExecutable? claude = ResolveInstalledExecutable(
            executableResolver,
            ClaudeExecutableProfile,
            InstalledExecutablePatterns.GetCandidates(
                TrustedExecutableKind.ClaudeCode,
                userProfile,
                localAppData));
        ResolvedExecutable? codex = ResolveInstalledExecutable(
            executableResolver,
            CodexExecutableProfile,
            InstalledExecutablePatterns.GetCandidates(
                TrustedExecutableKind.Codex,
                userProfile,
                localAppData));
        ResolvedExecutable? terminal = ResolveInstalledExecutable(
            executableResolver,
            TerminalExecutableProfile,
            InstalledExecutablePatterns.GetCandidates(
                TrustedExecutableKind.WindowsTerminal,
                userProfile,
                localAppData));
        var planRevalidator = new ResumePlanRevalidator(
            localPathPolicy,
            executableResolver);
        var router = new SessionActionRouter(
            new ResumeCommandRevalidator(localPathPolicy, executableResolver),
            new ResumePlanner(planRevalidator),
            new SystemProcessLauncher(planRevalidator),
            new PrivateClipboard(
                new WindowsPrivateClipboardNativeApi(),
                new WindowsStaThreadRunner(),
                new ClipboardRetryDelay()));
        var claudeScanner = new ClaudeLiveActivityScanner(
            new ClaudeActivityMarkerDiscovery(
                localPathPolicy,
                new PhysicalReadOnlyActivityFileSystem()),
            new WindowsProcessSnapshotSource());
        var codexDiscovery = new CodexLiveActivityDiscovery(
            localPathPolicy,
            new CodexWriterLockDetector(localPathPolicy, new ExclusiveFileProbe()));
        return new ActionComposition(
            localPathPolicy,
            router,
            claudeScanner,
            codexDiscovery,
            claude,
            codex,
            terminal);
    }

    private static ResolvedExecutable? ResolveInstalledExecutable(
        TrustedExecutableResolver resolver,
        TrustedExecutableProfile profile,
        IReadOnlyList<string> candidates)
    {
        try
        {
            return resolver.Resolve(profile, candidates).Executable;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                System.ComponentModel.Win32Exception or
                System.Security.Cryptography.CryptographicException or
                ArgumentException)
        {
            return null;
        }
    }

    private void ApplyActionComposition(ActionComposition composition)
    {
        pathPolicy = composition.PathPolicy;
        actionRouter = composition.Router;
        claudeActivityScanner = composition.ClaudeActivityScanner;
        codexActivityDiscovery = composition.CodexActivityDiscovery;
        claudeExecutable = composition.ClaudeExecutable;
        codexExecutable = composition.CodexExecutable;
        windowsTerminal = composition.WindowsTerminal;
    }

    private async Task RunIndexingAsync()
    {
        if (indexer is null)
        {
            return;
        }

        if (indexingUiRunning)
        {
            rescanPending = true;
            return;
        }

        indexingUiRunning = true;
        rescanButton.Enabled = false;
        Progress<IndexingProgress> progress = new(value =>
        {
            telemetry.SetProgress(
                $"{value.Stage}, {value.CompletedSessions}/{value.DiscoveredSessions} sessions");
            if (value.Stage == "Metadata ready")
            {
                telemetry.RecordFirstMetadataReady();
                indexStateLabel.Text =
                    $"Index: metadata ready, {value.DiscoveredSessions} sessions";
            }
            else
            {
                indexStateLabel.Text =
                    $"Index: {value.CompletedSessions}/{value.DiscoveredSessions} sessions, {FormatBytes(value.ProcessedBytes)} read";
            }
        });
        try
        {
            IndexingReport report = await indexer.ReconcileAsync(progress, lifetime.Token);
            lastIndexingReport = report;
            indexStateLabel.Text = report.IsPartial
                ? $"Index: Partial, {report.CompletedSessions} sessions, metadata {report.MetadataElapsed.TotalMilliseconds:0} ms"
                : $"Index: Ready, {report.CompletedSessions} sessions, metadata {report.MetadataElapsed.TotalMilliseconds:0} ms";
            telemetry.SetProgress(report.IsPartial
                ? $"Partial, {report.CompletedSessions} sessions"
                : $"Ready, {report.CompletedSessions} sessions");
            statusLabel.Text = report.ChangedSources == 0
                ? "Index is current."
                : $"Indexed {report.ChangedSources} changed sources in {report.Elapsed.TotalSeconds:0.0} seconds.";
            if (report.ChangedSources > 0)
            {
                await RunSearchAsync(resetPage: false, lifetime.Token);
            }
            else
            {
                await RefreshAvailabilityAsync(lifetime.Token);
            }
            await ReloadFavoriteDirectoriesAsync(lifetime.Token);
            await RefreshIndexStatusAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (InvalidOperationException)
        {
            statusLabel.Text = "A scan is already running.";
        }
        catch (Exception exception)
        {
            indexStateLabel.Text = "Index: Partial";
            statusLabel.Text = $"Rescan stopped: {SafeError(exception)}";
        }
        finally
        {
            indexingUiRunning = false;
            ScheduleIdleMaintenance();
            if (!closing)
            {
                rescanButton.Enabled = true;
                if (rescanPending)
                {
                    rescanPending = false;
                    providerChangeTimer.Stop();
                    providerChangeTimer.Start();
                }
            }
        }
    }

    private void StartProviderMonitoring()
    {
        if (providerMonitoringStarted || pathPolicy is null)
        {
            return;
        }

        providerMonitoringStarted = true;
        foreach (string configuredRoot in new[] { options.ClaudeRoot, options.CodexRoot })
        {
            LocalPathValidation validation = pathPolicy.ValidateExistingDirectory(configuredRoot);
            if (!validation.IsSafe)
            {
                continue;
            }

            try
            {
                var watcher = new FileSystemWatcher(validation.CanonicalPath!)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName |
                        NotifyFilters.DirectoryName |
                        NotifyFilters.LastWrite |
                        NotifyFilters.Size,
                    Filter = "*",
                };
                watcher.Changed += ProviderFileChanged;
                watcher.Created += ProviderFileChanged;
                watcher.Deleted += ProviderFileChanged;
                watcher.Renamed += ProviderFileChanged;
                watcher.Error += ProviderWatcherError;
                watcher.EnableRaisingEvents = true;
                providerWatchers.Add(watcher);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                indexStateLabel.Text = "Index: Ready, periodic scan only";
            }
        }

        periodicRescanTimer.Start();
    }

    private void ProviderFileChanged(object sender, FileSystemEventArgs e)
    {
        if (closing || !IsProviderIndexFile(e.FullPath))
        {
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                providerChangeTimer.Stop();
                providerChangeTimer.Start();
            });
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ProviderWatcherError(object sender, ErrorEventArgs e)
    {
        if (closing)
        {
            return;
        }

        try
        {
            BeginInvoke(() =>
            {
                indexStateLabel.Text = "Index: watcher interrupted, periodic scan active";
                rescanPending = true;
            });
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void ProviderChangeTimerTick(object? sender, EventArgs e)
    {
        providerChangeTimer.Stop();
        _ = RunIndexingAsync();
    }

    private void PeriodicRescanTimerTick(object? sender, EventArgs e) => _ = RunIndexingAsync();

    private static bool IsProviderIndexFile(string path)
    {
        string extension = Path.GetExtension(path);
        return extension.Equals(".jsonl", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".json", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".lock", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sqlite", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".sqlite3", StringComparison.OrdinalIgnoreCase);
    }

    private Task RunSearchAsync(bool resetPage, CancellationToken outerToken) =>
        resetPage || activeResultQuery is null
            ? BeginSearchGenerationAsync(outerToken)
            : RefreshCurrentGenerationAsync(outerToken);

    private async Task BeginSearchGenerationAsync(CancellationToken outerToken)
    {
        if (searchService is null)
        {
            return;
        }

        QueryParseResult parsed = QueryParser.Parse(searchBox.Text);
        if (!parsed.IsSuccess)
        {
            statusLabel.Text = parsed.Error!.Message;
            return;
        }

        activeSearch?.Cancel();
        activeSearch?.Dispose();
        activeSearch = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        CancellationToken cancellationToken = activeSearch.Token;
        var query = new SearchGeneration(
            ++nextSearchGeneration,
            parsed.Query!,
            SelectedScope(),
            selectedFavoriteDirectory);
        activeResultQuery = query;
        virtualResults.BeginGeneration(query.Id);
        currentAvailability = [];
        restoringResultState = true;
        resultList.BeginUpdate();
        try
        {
            resultList.VirtualListSize = 0;
            resultList.Invalidate();
        }
        finally
        {
            resultList.EndUpdate();
            restoringResultState = false;
        }

        resultCountLabel.Text = "0 sessions";
        ShowEmptyDetails();
        Interlocked.Increment(ref activeSearchCount);
        try
        {
            if (!query.Query.IsBrowse)
            {
                Stopwatch metadataClock = Stopwatch.StartNew();
                SessionSearchPage metadataPage = await searchService.SearchAsync(
                    query.CreateRequest(0, SearchContentMode.MetadataOnly),
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                telemetry.RecordQuery(
                    metadataClock.Elapsed.TotalMilliseconds,
                    transcriptCapable: false);
                ApplyResultPage(
                    query,
                    0,
                    metadataPage,
                    $"Metadata ready in {metadataClock.ElapsedMilliseconds} ms. Searching transcripts.");
            }

            Stopwatch completeClock = Stopwatch.StartNew();
            SessionSearchPage page = await searchService.SearchAsync(
                query.CreateRequest(0, SearchContentMode.All),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            telemetry.RecordQuery(
                completeClock.Elapsed.TotalMilliseconds,
                transcriptCapable: !query.Query.IsBrowse);
            string state = page.IsPartial ? "Partial index" : "Index current";
            ApplyResultPage(
                query,
                0,
                page,
                $"{state}. Query completed in {completeClock.ElapsedMilliseconds} ms.");
            await RefreshAvailabilityAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            statusLabel.Text = $"Search stopped: {SafeError(exception)}";
        }
        finally
        {
            Interlocked.Decrement(ref activeSearchCount);
            ScheduleIdleMaintenance();
        }
    }

    private async Task RefreshCurrentGenerationAsync(CancellationToken outerToken)
    {
        SearchGeneration? query = activeResultQuery;
        SessionSearchService? service = searchService;
        CancellationTokenSource? generationCancellation = activeSearch;
        if (query is null || service is null || generationCancellation is null)
        {
            return;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            outerToken,
            generationCancellation.Token);
        CancellationToken cancellationToken = linked.Token;
        HashSet<int> pages = [0];
        int lastPage = virtualResults.TotalCount == 0
            ? 0
            : (virtualResults.TotalCount - 1) / virtualResults.PageSize;
        foreach (int loadedPage in virtualResults.LoadedPageNumbers)
        {
            pages.Add(loadedPage);
            pages.Add(Math.Max(0, loadedPage - 1));
            pages.Add(Math.Min(lastPage, loadedPage + 1));
        }

        Interlocked.Increment(ref activeSearchCount);
        try
        {
            Stopwatch clock = Stopwatch.StartNew();
            using var refreshGate = new SemaphoreSlim(4, 4);
            Task<(int PageNumber, SessionSearchPage Page)>[] pageTasks = pages
                .Order()
                .Select(async pageNumber =>
                {
                    await refreshGate.WaitAsync(cancellationToken);
                    try
                    {
                        SessionSearchPage page = await service.SearchAsync(
                            query.CreateRequest(pageNumber, SearchContentMode.All),
                            cancellationToken);
                        return (pageNumber, page);
                    }
                    finally
                    {
                        refreshGate.Release();
                    }
                })
                .ToArray();
            (int PageNumber, SessionSearchPage Page)[] refreshed = await Task.WhenAll(pageTasks);
            cancellationToken.ThrowIfCancellationRequested();
            if (activeResultQuery?.Id != query.Id)
            {
                return;
            }

            ResultListState interaction = CaptureResultListState();
            bool changed = false;
            IReadOnlySet<int> protectedPages = GetProtectedResultPages();
            foreach ((int pageNumber, SessionSearchPage page) in refreshed)
            {
                changed |= virtualResults.ApplyPage(
                    query.Id,
                    pageNumber,
                    page,
                    protectedPages);
            }

            if (changed)
            {
                ApplyResultSurface(interaction);
            }

            SessionSearchPage first = refreshed.First(item => item.PageNumber == 0).Page;
            telemetry.RecordQuery(
                clock.Elapsed.TotalMilliseconds,
                transcriptCapable: !query.Query.IsBrowse);
            resultCountLabel.Text = $"{virtualResults.TotalCount:N0} sessions";
            statusLabel.Text = first.IsPartial
                ? "Partial index. Loaded results refreshed without moving your view."
                : "Index current. Loaded results refreshed without moving your view.";
            await RefreshAvailabilityAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            statusLabel.Text = $"Result refresh stopped: {SafeError(exception)}";
        }
        finally
        {
            Interlocked.Decrement(ref activeSearchCount);
            ScheduleIdleMaintenance();
        }
    }

    private void ApplyResultPage(
        SearchGeneration query,
        int pageNumber,
        SessionSearchPage page,
        string status)
    {
        if (activeResultQuery?.Id != query.Id)
        {
            return;
        }

        ResultListState interaction = CaptureResultListState();
        bool changed = virtualResults.ApplyPage(
            query.Id,
            pageNumber,
            page,
            GetProtectedResultPages());
        if (changed)
        {
            ApplyResultSurface(interaction);
        }

        resultCountLabel.Text = $"{virtualResults.TotalCount:N0} sessions";
        statusLabel.Text = status;
        if (pageNumber == 0 && page.Results.Count > 0)
        {
            telemetry.RecordFirstUsableRows();
        }
        else if (virtualResults.TotalCount == 0)
        {
            ShowEmptyDetails();
        }
    }

    private void ApplyResultSurface(ResultListState interaction)
    {
        restoringResultState = true;
        resultList.BeginUpdate();
        try
        {
            if (resultList.VirtualListSize != virtualResults.TotalCount)
            {
                resultList.VirtualListSize = virtualResults.TotalCount;
            }

            resultList.Invalidate();
            RestoreResultListState(interaction);
        }
        finally
        {
            resultList.EndUpdate();
            restoringResultState = false;
        }

        ShowSelection();
    }

    private void ApplyLazyResultPage(
        SearchGeneration query,
        int pageNumber,
        SessionSearchPage page)
    {
        if (activeResultQuery?.Id != query.Id)
        {
            return;
        }

        bool sizeChanged = resultList.VirtualListSize != page.TotalCount;
        ResultListState interaction = sizeChanged
            ? CaptureResultListState()
            : ResultListState.Empty;
        bool changed = virtualResults.ApplyPage(
            query.Id,
            pageNumber,
            page,
            GetProtectedResultPages());
        if (!changed)
        {
            return;
        }

        resultCountLabel.Text = $"{virtualResults.TotalCount:N0} sessions";
        if (sizeChanged)
        {
            ApplyResultSurface(interaction);
        }
        else
        {
            resultList.Invalidate();
            ShowSelection();
        }
    }

    private HashSet<int> GetProtectedResultPages()
    {
        HashSet<int> pages = resultList.SelectedIndices
            .Cast<int>()
            .Select(index => index / virtualResults.PageSize)
            .ToHashSet();
        int focusedIndex = resultList.FocusedItem?.Index ?? -1;
        if (focusedIndex >= 0)
        {
            pages.Add(focusedIndex / virtualResults.PageSize);
        }

        try
        {
            int topIndex = resultList.TopItem?.Index ?? -1;
            if (topIndex >= 0)
            {
                int topPage = topIndex / virtualResults.PageSize;
                pages.Add(topPage);
                pages.Add(topPage + 1);
            }
        }
        catch (InvalidOperationException)
        {
        }

        return pages;
    }

    private ResultListState CaptureResultListState()
    {
        SessionIdentity[] selectedIdentities = resultList.SelectedIndices
            .Cast<int>()
            .Order()
            .Select(index => virtualResults.TryGet(index, out SessionSearchResult? result)
                ? result?.Session.Identity
                : null)
            .OfType<SessionIdentity>()
            .ToArray();
        int focusedIndex = resultList.FocusedItem?.Index ?? -1;
        SessionIdentity? focusedIdentity =
            virtualResults.TryGet(focusedIndex, out SessionSearchResult? focused)
                ? focused?.Session.Identity
                : null;
        int topIndex = 0;
        try
        {
            topIndex = resultList.TopItem?.Index ?? 0;
        }
        catch (InvalidOperationException)
        {
        }

        SessionIdentity? topIdentity =
            virtualResults.TryGet(topIndex, out SessionSearchResult? top)
                ? top?.Session.Identity
                : null;
        return new ResultListState(
            selectedIdentities,
            focusedIdentity,
            topIdentity,
            topIndex);
    }

    private void RestoreResultListState(ResultListState state)
    {
        resultList.SelectedIndices.Clear();
        foreach (SessionIdentity identity in state.SelectedIdentities)
        {
            int selectedIndex = virtualResults.FindIndex(identity);
            if ((uint)selectedIndex < (uint)resultList.VirtualListSize)
            {
                resultList.Items[selectedIndex].Selected = true;
            }
        }

        if (state.FocusedIdentity.HasValue)
        {
            int focusedIndex = virtualResults.FindIndex(state.FocusedIdentity.Value);
            if ((uint)focusedIndex < (uint)resultList.VirtualListSize)
            {
                resultList.Items[focusedIndex].Focused = true;
            }
        }

        if (resultList.VirtualListSize == 0)
        {
            return;
        }

        int topIndex = state.TopIdentity.HasValue
            ? virtualResults.FindIndex(state.TopIdentity.Value)
            : -1;
        if (topIndex < 0)
        {
            topIndex = Math.Clamp(state.TopIndex, 0, resultList.VirtualListSize - 1);
        }

        try
        {
            resultList.TopItem = resultList.Items[topIndex];
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static Icon LoadApplicationIcon()
    {
        using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream(
            "SessionSearch.App.Assets.SessionSearch.ico");
        if (stream is null)
        {
            return (Icon)SystemIcons.Application.Clone();
        }

        using var icon = new Icon(stream);
        return (Icon)icon.Clone();
    }

    private void ScheduleIdleMaintenance()
    {
        if (closing)
        {
            return;
        }

        idleMaintenanceTimer.Stop();
        idleMaintenanceTimer.Start();
    }

    private async void IdleMaintenanceTimerTick(object? sender, EventArgs e)
    {
        idleMaintenanceTimer.Stop();
        if (closing)
        {
            return;
        }

        if (indexingUiRunning || Volatile.Read(ref activeSearchCount) != 0)
        {
            ScheduleIdleMaintenance();
            return;
        }

        if (database is not null)
        {
            await IdleResourceMaintenance.TryReleaseTransientResourcesAsync(database);
        }
    }

    private async Task RefreshAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (pathPolicy is null)
        {
            currentAvailability = new Dictionary<SessionIdentity, AvailabilityDecision>();
            return;
        }

        SessionSearchResult[] snapshot = virtualResults.GetLoadedResults();
        ActivityContext activity = await CaptureActivityContextAsync(
            snapshot.Select(result => result.Session).ToArray(),
            cancellationToken);
        Dictionary<SessionIdentity, AvailabilityDecision> decisions = await Task.Run(
            () => snapshot.ToDictionary(
                result => result.Session.Identity,
                result => EvaluateAvailability(result.Session, activity)),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        currentAvailability = decisions;
        unmappedClaudeActivity = activity.ClaudeSnapshot?.HasUnmappedClaudeActivity ?? false;
        telemetry.SetUnmappedClaudeActivity(unmappedClaudeActivity);
        UpdateUnmappedClaudeWarning();
        resultList.Invalidate();
        ShowSelection();
    }

    private void ScheduleAvailabilityRefresh()
    {
        if (closing)
        {
            return;
        }

        availabilityRefreshTimer.Stop();
        availabilityRefreshTimer.Start();
    }

    private async void AvailabilityRefreshTimerTick(object? sender, EventArgs e)
    {
        availabilityRefreshTimer.Stop();
        if (closing)
        {
            return;
        }

        try
        {
            await RefreshAvailabilityAsync(lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            statusLabel.Text = $"Session state refresh stopped: {SafeError(exception)}";
        }
    }

    private AvailabilityDecision EvaluateAvailability(
        SessionDocument session,
        ActivityContext? activity = null)
    {
        bool directorySafe = false;
        bool directoryExists = false;
        if (pathPolicy is not null)
        {
            LocalPathValidation lexical = LocalPathPolicy.ValidateLexically(session.Directory);
            if (lexical.IsSafe)
            {
                LocalPathValidation physical = pathPolicy.ValidateExistingDirectory(
                    lexical.CanonicalPath!);
                if (physical.IsSafe && string.Equals(
                    physical.CanonicalPath,
                    lexical.CanonicalPath,
                    StringComparison.OrdinalIgnoreCase))
                {
                    directorySafe = true;
                    directoryExists = true;
                }
                else if (physical.Failure == LocalPathFailure.Missing)
                {
                    directorySafe = true;
                }
            }
        }

        ActiveSessionState activeState = ActiveSessionState.Inactive;
        if (activity?.ClaudeSnapshot is not null &&
            session.Identity.Provider == SessionProvider.ClaudeCode)
        {
            activeState = activity.ClaudeSnapshot.Detect(session.Identity).State;
        }
        else if (activity is not null &&
            codexActivityDiscovery is not null &&
            session.Identity.Provider == SessionProvider.Codex)
        {
            activity.CodexChildren.TryGetValue(
                session.Identity,
                out IReadOnlyList<Guid>? childIds);
            activeState = codexActivityDiscovery.Detect(
                options.CodexRoot,
                session.Identity,
                childIds ?? []).State;
        }

        return AvailabilityEvaluator.Evaluate(new AvailabilityInputs(
            FormatSupported: session.FormatSupported,
            SourcePresent: session.SourcePresent,
            Archived: session.Archived,
            Active: activeState == ActiveSessionState.Active,
            PossiblyActive: activeState == ActiveSessionState.PossiblyActive,
            DirectorySafe: directorySafe,
            DirectoryExists: directoryExists,
            CliExists: ProviderExecutable(session.Identity.Provider) is not null));
    }

    private async Task<ActivityContext> CaptureActivityContextAsync(
        SessionDocument[] sessions,
        CancellationToken cancellationToken)
    {
        Task<ClaudeLiveActivitySnapshot?> claudeTask = Task.Run(
            ScanClaudeActivity,
            cancellationToken);
        IReadOnlyDictionary<SessionIdentity, IReadOnlyList<Guid>> codexChildren =
            database is null
                ? new Dictionary<SessionIdentity, IReadOnlyList<Guid>>()
                : await new SessionRepository(database).ListChildSessionIdsAsync(
                    sessions.Select(session => session.Identity).ToArray(),
                    cancellationToken);
        ClaudeLiveActivitySnapshot? claudeSnapshot = await claudeTask;
        return new ActivityContext(claudeSnapshot, codexChildren);
    }

    private ClaudeLiveActivitySnapshot? ScanClaudeActivity()
    {
        if (claudeActivityScanner is null || claudeExecutable is null)
        {
            return null;
        }

        try
        {
            return claudeActivityScanner.Scan(options.ClaudeRoot, claudeExecutable);
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidOperationException or
                System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private async Task ReloadFavoriteDirectoriesAsync(CancellationToken cancellationToken)
    {
        if (favorites is null)
        {
            return;
        }

        IReadOnlyList<string> paths = await favorites.ListDirectoryFavoritesAsync(
            cancellationToken);
        string? previousSelection = selectedFavoriteDirectory;
        reloadingFavoriteDirectories = true;
        favoriteDirectories.BeginUpdate();
        try
        {
            favoriteDirectories.Items.Clear();
            favoriteDirectories.Items.Add(AllDirectoriesLabel);
            foreach (string path in paths)
            {
                favoriteDirectories.Items.Add(path);
            }

            int selectedIndex = previousSelection is null
                ? 0
                : favoriteDirectories.Items.IndexOf(previousSelection);
            if (selectedIndex < 0)
            {
                selectedIndex = 0;
                selectedFavoriteDirectory = null;
            }

            favoriteDirectories.SelectedIndex = selectedIndex;
        }
        finally
        {
            favoriteDirectories.EndUpdate();
            reloadingFavoriteDirectories = false;
        }
    }

    private void SearchBoxTextChanged(object? sender, EventArgs e)
    {
        searchTimer.Stop();
        searchTimer.Start();
    }

    private void SearchTimerTick(object? sender, EventArgs e)
    {
        searchTimer.Stop();
        _ = RunSearchAsync(resetPage: true, lifetime.Token);
    }

    private void ScopeBoxSelectedIndexChanged(object? sender, EventArgs e) =>
        _ = RunSearchAsync(resetPage: true, lifetime.Token);

    private void RescanButtonClick(object? sender, EventArgs e) => _ = RunIndexingAsync();

    private void FavoriteDirectoriesSelectedIndexChanged(object? sender, EventArgs e)
    {
        if (reloadingFavoriteDirectories)
        {
            return;
        }

        selectedFavoriteDirectory = favoriteDirectories.SelectedItem is string directory &&
            !string.Equals(directory, AllDirectoriesLabel, StringComparison.Ordinal)
                ? directory
                : null;
        _ = RunSearchAsync(resetPage: true, lifetime.Token);
    }

    private void ResultListRetrieveVirtualItem(
        object? sender,
        RetrieveVirtualItemEventArgs e)
    {
        if (!virtualResults.TryGet(e.ItemIndex, out SessionSearchResult? result) ||
            result is null)
        {
            e.Item = CreateLoadingItem();
            QueueVirtualPage(e.ItemIndex / virtualResults.PageSize);
            return;
        }

        string provider = result.Session.Identity.Provider == SessionProvider.ClaudeCode
            ? "Claude Code"
            : "Codex";
        string state = SessionState(result.Session);
        string sessionAndRequest = string.IsNullOrWhiteSpace(result.Session.Description)
            ? result.Session.Title
            : $"{result.Session.Title} | {result.Session.Description}";
        string directoryAndBranch = string.IsNullOrWhiteSpace(result.Session.Branch)
            ? result.Session.Directory
            : $"{result.Session.Directory}  ({result.Session.Branch})";
        ListViewItem item = new(sessionAndRequest)
        {
            ToolTipText = result.Session.Description,
        };
        item.SubItems.Add(provider);
        item.SubItems.Add(directoryAndBranch);
        item.SubItems.Add(FormatRelativeTime(result.Session.LastActivityUtc));
        item.SubItems.Add(state);
        if (result.IsSessionFavorite || result.IsDirectoryFavorite)
        {
            item.ForeColor = Signal;
            item.Font = favoriteRowFont;
        }

        e.Item = item;
    }

    private void ResultListCacheVirtualItems(object? sender, CacheVirtualItemsEventArgs e)
    {
        if (virtualResults.TotalCount == 0)
        {
            return;
        }

        int firstPage = Math.Max(0, (e.StartIndex / virtualResults.PageSize) - 1);
        int lastPage = Math.Min(
            (virtualResults.TotalCount - 1) / virtualResults.PageSize,
            (e.EndIndex / virtualResults.PageSize) + 1);
        for (int pageNumber = firstPage; pageNumber <= lastPage; pageNumber++)
        {
            QueueVirtualPage(pageNumber);
        }
    }

    private static ListViewItem CreateLoadingItem()
    {
        ListViewItem item = new("Loading session...")
        {
            ForeColor = Smoke,
            ToolTipText = "This session page is loading from the local index.",
        };
        item.SubItems.Add(string.Empty);
        item.SubItems.Add(string.Empty);
        item.SubItems.Add(string.Empty);
        item.SubItems.Add("Loading");
        return item;
    }

    private void QueueVirtualPage(int pageNumber)
    {
        SearchGeneration? query = activeResultQuery;
        if (query is null || searchService is null || activeSearch is null ||
            !virtualResults.TryBeginPageRequest(query.Id, pageNumber))
        {
            return;
        }

        _ = LoadVirtualPageAsync(query, pageNumber, activeSearch.Token);
    }

    private async Task LoadVirtualPageAsync(
        SearchGeneration query,
        int pageNumber,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref activeSearchCount);
        bool enteredGate = false;
        try
        {
            await pageLoadGate.WaitAsync(cancellationToken);
            enteredGate = true;
            SessionSearchService? service = searchService;
            if (service is null)
            {
                return;
            }

            SessionSearchPage page = await service.SearchAsync(
                query.CreateRequest(pageNumber, SearchContentMode.All),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            ApplyLazyResultPage(query, pageNumber, page);
            ScheduleAvailabilityRefresh();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            statusLabel.Text = $"A result page could not be loaded: {SafeError(exception)}";
        }
        finally
        {
            if (enteredGate)
            {
                pageLoadGate.Release();
            }

            virtualResults.EndPageRequest(query.Id, pageNumber);
            Interlocked.Decrement(ref activeSearchCount);
            ScheduleIdleMaintenance();
        }
    }

    private void ResultListItemSelectionChanged(
        object? sender,
        ListViewItemSelectionChangedEventArgs e)
    {
        if (!restoringResultState)
        {
            ShowSelection();
        }
    }

    private void ResultListDoubleClick(object? sender, EventArgs e) => _ = OpenFocusedSessionAsync();

    private void ShowSelection()
    {
        SessionSearchResult[] selectedResults = SelectedResults();
        if (selectedResults.Length == 0)
        {
            ShowEmptyDetails();
            return;
        }

        if (selectedResults.Length > 1)
        {
            SessionActionSelectionSummary summary = SessionActionRouter.Summarize(
                selectedResults.Select(CreateActionCandidate));
            detailTitle.Text = $"{selectedResults.Length} sessions selected";
            detailDescription.Text =
                $"{summary.Ready} ready, {summary.ActiveOrPossiblyActive} active or possibly active, {summary.Duplicate} duplicate, {summary.OtherUnavailable} unavailable";
            detailProvider.Text = "BATCH SELECTION";
            detailDirectory.Text = "Ready sessions open in visible selection order.";
            detailIdentity.Text = "Duplicate provider and session IDs are opened once.";
            detailMatch.Text = "Blocked sessions are skipped and reported by category.";
            commandPreview.Text = FormatCommandPreview(selectedResults);
            sessionStarButton.Enabled = false;
            directoryStarButton.Enabled = false;
            sessionStarButton.Text = "Star session";
            directoryStarButton.Text = "Star directory";
            UpdateActionButtons(summary);
            statusLabel.Text =
                $"Selection: {summary.Ready} ready, {summary.ActiveOrPossiblyActive} active, {summary.Duplicate} duplicate, {summary.OtherUnavailable} unavailable.";
            return;
        }

        SessionSearchResult selected = selectedResults[0];

        SessionDocument session = selected.Session;
        string provider = session.Identity.Provider == SessionProvider.ClaudeCode
            ? "Claude Code"
            : "Codex";
        detailTitle.Text = session.Title;
        detailDescription.Text = session.Description;
        detailDirectory.Text = $"Directory\r\n{session.Directory}";
        string created = session.CreatedUtc.HasValue
            ? session.CreatedUtc.Value.UtcDateTime.ToString(
                "yyyy-MM-dd HH:mm:ss 'UTC'",
                CultureInfo.InvariantCulture)
            : "Unknown";
        string updated = session.LastActivityUtc.UtcDateTime.ToString(
            "yyyy-MM-dd HH:mm:ss 'UTC'",
            CultureInfo.InvariantCulture);
        detailIdentity.Text = $"""
            ID: {session.Identity.SessionId:D}
            Created: {created}
            Updated: {updated}
            Model: {session.Model ?? "Unknown"}
            Branch: {session.Branch ?? "Unknown"}
            Source size: {FormatBytes(session.SourceBytes)}
            """.ReplaceLineEndings();
        detailMatch.Text = selected.Snippet is null
            ? "Metadata match"
            : selected.SnippetFromChild
                ? $"Child-log match excerpt\r\n{selected.Snippet}"
                : $"Match excerpt\r\n{selected.Snippet}";
        AvailabilityDecision availability = AvailabilityFor(session);
        detailProvider.Text = $"{provider}  /  {AvailabilityLabel(availability.Status)}";
        if (availability.Status != AvailabilityStatus.Ready)
        {
            detailMatch.Text = $"{availability.Reason}\r\n{availability.SafeAction}";
        }

        commandPreview.Text = FormatCommandPreview([selected]);

        sessionStarButton.Enabled = true;
        directoryStarButton.Enabled = true;
        sessionStarButton.Text = selected.IsSessionFavorite
            ? "Unstar session"
            : "Star session";
        directoryStarButton.Text = selected.IsDirectoryFavorite
            ? "Unstar directory"
            : "Star directory";
        UpdateActionButtons(SessionActionRouter.Summarize([CreateActionCandidate(selected)]));
    }

    private void UpdateActionButtons(SessionActionSelectionSummary summary)
    {
        copyButton.Text = summary.Total > 1 ? "Copy all" : "Copy";
        openButton.Text = summary.Total > 1 ? "Open tabs" : "Open";
        copyButton.Enabled = actionRouter is not null && summary.Ready > 0;
        openButton.Enabled = actionRouter is not null &&
            windowsTerminal is not null &&
            summary.Ready > 0;
    }

    private void ShowEmptyDetails()
    {
        detailTitle.Text = "Select a session";
        detailDescription.Text = "Search or browse the local index, then select a row for details and resume actions.";
        detailProvider.Text = string.Empty;
        detailDirectory.Text = string.Empty;
        detailIdentity.Text = string.Empty;
        detailMatch.Text = string.Empty;
        commandPreview.Text = string.Empty;
        sessionStarButton.Enabled = false;
        directoryStarButton.Enabled = false;
        copyButton.Enabled = false;
        openButton.Enabled = false;
    }

    private async void SessionStarButtonClick(object? sender, EventArgs e)
    {
        SessionSearchResult? selected = SelectedResult();
        if (selected is null || favorites is null)
        {
            return;
        }

        try
        {
            await favorites.SetSessionFavoriteAsync(
                selected.Session.Identity,
                !selected.IsSessionFavorite,
                lifetime.Token);
            await RunSearchAsync(resetPage: false, lifetime.Token);
            statusLabel.Text = selected.IsSessionFavorite
                ? "Session favorite removed."
                : "Session favorite saved.";
        }
        catch (Exception exception)
        {
            statusLabel.Text = $"Favorite was not saved: {SafeError(exception)}";
        }
    }

    private async void DirectoryStarButtonClick(object? sender, EventArgs e)
    {
        SessionSearchResult? selected = SelectedResult();
        if (selected is null || favorites is null)
        {
            return;
        }

        try
        {
            await favorites.SetDirectoryFavoriteAsync(
                selected.Session.Directory,
                !selected.IsDirectoryFavorite,
                lifetime.Token);
            await ReloadFavoriteDirectoriesAsync(lifetime.Token);
            await RunSearchAsync(resetPage: false, lifetime.Token);
            statusLabel.Text = selected.IsDirectoryFavorite
                ? "Directory favorite removed."
                : "Directory favorite saved.";
        }
        catch (Exception exception)
        {
            statusLabel.Text = $"Favorite was not saved: {SafeError(exception)}";
        }
    }

    private async void CopyButtonClick(object? sender, EventArgs e)
    {
        await CopySelectionAsync();
    }

    private async void OpenButtonClick(object? sender, EventArgs e)
    {
        await OpenSelectionAsync(SelectedResults());
    }

    private async Task CopySelectionAsync()
    {
        SessionSearchResult[] selected = SelectedResults();
        if (selected.Length == 0 || actionRouter is not SessionActionRouter router)
        {
            statusLabel.Text = "Select at least one session after resume actions finish loading.";
            return;
        }

        try
        {
            SessionBatchActionResult result = await router.CopyAsync(
                await CreateFreshActionCandidatesAsync(selected, lifetime.Token),
                lifetime.Token);
            statusLabel.Text = result.Message;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            statusLabel.Text = $"Commands were not copied: {SafeError(exception)}";
        }
    }

    private async Task OpenSelectionAsync(SessionSearchResult[] selected)
    {
        if (selected.Length == 0 || actionRouter is not SessionActionRouter router)
        {
            statusLabel.Text = "Select at least one session after resume actions finish loading.";
            return;
        }

        try
        {
            SessionBatchActionResult result = await router.OpenAsync(
                await CreateFreshActionCandidatesAsync(selected, lifetime.Token),
                windowsTerminal,
                lifetime.Token);
            statusLabel.Text = result.Message;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            statusLabel.Text = $"Terminal tabs were not opened: {SafeError(exception)}";
        }
    }

    private async Task OpenFocusedSessionAsync()
    {
        SessionSearchResult? focused = FocusedResult();
        if (focused is not null)
        {
            await OpenSelectionAsync([focused]);
        }
    }

    private async Task<SessionActionCandidate[]> CreateFreshActionCandidatesAsync(
        SessionSearchResult[] results,
        CancellationToken cancellationToken)
    {
        SessionSearchResult[] snapshot = results.ToArray();
        ActivityContext activity = await CaptureActivityContextAsync(
            snapshot.Select(result => result.Session).ToArray(),
            cancellationToken);
        SessionActionCandidate[] candidates = await Task.Run(
            () => snapshot
                .Select(result => new SessionActionCandidate(
                    result.Session.Identity,
                    result.Session.Directory,
                    EvaluateAvailability(result.Session, activity),
                    ProviderExecutable(result.Session.Identity.Provider)))
                .ToArray(),
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        Dictionary<SessionIdentity, AvailabilityDecision> updated =
            currentAvailability.ToDictionary(pair => pair.Key, pair => pair.Value);
        foreach (SessionActionCandidate candidate in candidates)
        {
            updated[candidate.Identity] = candidate.Availability;
        }

        currentAvailability = updated;
        resultList.Invalidate();
        ShowSelection();
        return candidates;
    }

    private void MainFormKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Control && e.Shift && e.KeyCode == Keys.C)
        {
            _ = CopySelectionAsync();
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.Shift && e.KeyCode == Keys.S)
        {
            SessionStarButtonClick(this, EventArgs.Empty);
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.D)
        {
            DirectoryStarButtonClick(this, EventArgs.Empty);
            e.SuppressKeyPress = true;
        }
        else if (e.Control && e.KeyCode == Keys.L)
        {
            searchBox.Focus();
            searchBox.SelectAll();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Down && searchBox.Focused && virtualResults.TotalCount > 0)
        {
            resultList.Focus();
            resultList.Items[0].Selected = true;
            resultList.Items[0].Focused = true;
            resultList.EnsureVisible(0);
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape)
        {
            if (selectedFavoriteDirectory is not null)
            {
                favoriteDirectories.SelectedIndex = 0;
            }
            else if (searchBox.TextLength > 0)
            {
                searchBox.Clear();
            }

            searchBox.Focus();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.F5)
        {
            _ = RunIndexingAsync();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Enter && resultList.Focused)
        {
            _ = OpenFocusedSessionAsync();
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Space && resultList.Focused)
        {
            SessionStarButtonClick(this, EventArgs.Empty);
            e.SuppressKeyPress = true;
        }
    }

    private void MainFormClosing(object? sender, FormClosingEventArgs e)
    {
        closing = true;
        searchTimer.Stop();
        providerChangeTimer.Stop();
        periodicRescanTimer.Stop();
        idleMaintenanceTimer.Stop();
        availabilityRefreshTimer.Stop();
        foreach (FileSystemWatcher watcher in providerWatchers)
        {
            watcher.Dispose();
        }

        providerWatchers.Clear();
        lifetime.Cancel();
        activeSearch?.Cancel();
        activeSearch?.Dispose();
        database?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        favoriteRowFont?.Dispose();
        favoriteRowFont = null;
        applicationIcon?.Dispose();
        applicationIcon = null;
        lifetime.Dispose();
    }

    private SessionSearchResult? SelectedResult()
    {
        if (resultList.SelectedIndices.Count != 1)
        {
            return null;
        }

        int index = resultList.SelectedIndices[0];
        return virtualResults.TryGet(index, out SessionSearchResult? result) ? result : null;
    }

    private SessionSearchResult[] SelectedResults() =>
        resultList.SelectedIndices
            .Cast<int>()
            .Order()
            .Select(index => virtualResults.TryGet(index, out SessionSearchResult? result)
                ? result
                : null)
            .OfType<SessionSearchResult>()
            .ToArray();

    private SessionSearchResult? FocusedResult()
    {
        int index = resultList.FocusedItem?.Index ?? -1;
        if (virtualResults.TryGet(index, out SessionSearchResult? focused))
        {
            return focused;
        }

        SessionSearchResult[] selected = SelectedResults();
        return selected.Length > 0 ? selected[0] : null;
    }

    private SessionActionCandidate CreateActionCandidate(SessionSearchResult result) => new(
        result.Session.Identity,
        result.Session.Directory,
        AvailabilityFor(result.Session),
        ProviderExecutable(result.Session.Identity.Provider));

    private string FormatCommandPreview(IEnumerable<SessionSearchResult> results)
    {
        HashSet<SessionIdentity> seen = [];
        List<string> commands = [];
        foreach (SessionSearchResult result in results)
        {
            SessionActionCandidate candidate = CreateActionCandidate(result);
            if (candidate.Availability.Status != AvailabilityStatus.Ready ||
                candidate.ProviderExecutable is null ||
                !seen.Add(candidate.Identity))
            {
                continue;
            }

            commands.Add(PowerShellCommandFormatter.Format(new ResumeCommand(
                candidate.Identity,
                candidate.WorkingDirectory,
                candidate.ProviderExecutable)));
        }

        return commands.Count == 0
            ? "No resume command is available for the current selection."
            : string.Join(Environment.NewLine, commands);
    }

    private AvailabilityDecision AvailabilityFor(SessionDocument session)
    {
        if (currentAvailability.TryGetValue(session.Identity, out AvailabilityDecision? decision))
        {
            return decision;
        }

        return AvailabilityEvaluator.Evaluate(new AvailabilityInputs(
            FormatSupported: session.FormatSupported,
            SourcePresent: session.SourcePresent,
            Archived: session.Archived,
            DirectorySafe: true,
            DirectoryExists: true,
            CliExists: false));
    }

    private ResolvedExecutable? ProviderExecutable(SessionProvider provider) => provider switch
    {
        SessionProvider.ClaudeCode => claudeExecutable,
        SessionProvider.Codex => codexExecutable,
        _ => null,
    };

    private SearchScope SelectedScope() => scopeBox.SelectedIndex switch
    {
        1 => SearchScope.ClaudeCode,
        2 => SearchScope.Codex,
        3 => SearchScope.Starred,
        _ => SearchScope.All,
    };

    private string SessionState(SessionDocument session)
    {
        if (currentAvailability.TryGetValue(session.Identity, out AvailabilityDecision? decision))
        {
            return AvailabilityLabel(decision.Status);
        }

        if (!session.FormatSupported)
        {
            return "Unsupported format";
        }

        if (!session.SourcePresent)
        {
            return "Source removed";
        }

        return session.Archived ? "Archived" : "Checking";
    }

    private static string AvailabilityLabel(AvailabilityStatus status) => status switch
    {
        AvailabilityStatus.UnsupportedFormat => "Unsupported format",
        AvailabilityStatus.SourceRemoved => "Source removed",
        AvailabilityStatus.Archived => "Archived",
        AvailabilityStatus.Active => "Active",
        AvailabilityStatus.PossiblyActive => "Possibly active",
        AvailabilityStatus.UnsafeDirectory => "Unsafe directory",
        AvailabilityStatus.MissingDirectory => "Missing directory",
        AvailabilityStatus.MissingCli => "Missing CLI",
        AvailabilityStatus.Ready => "Ready",
        _ => "Unavailable",
    };

    private static string FormatRelativeTime(DateTimeOffset time)
    {
        TimeSpan age = DateTimeOffset.UtcNow - time;
        if (age.TotalMinutes < 1)
        {
            return "just now";
        }

        if (age.TotalHours < 1)
        {
            return $"{Math.Floor(age.TotalMinutes):0} min ago";
        }

        if (age.TotalDays < 1)
        {
            return $"{Math.Floor(age.TotalHours):0} hr ago";
        }

        if (age.TotalDays < 30)
        {
            return $"{Math.Floor(age.TotalDays):0} days ago";
        }

        return time.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024d:0.0} KiB";
        }

        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / (1024d * 1024):0.0} MiB";
        }

        return $"{bytes / (1024d * 1024 * 1024):0.0} GiB";
    }

    private static string SafeError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "access was denied",
        IOException => "a local file could not be read",
        SessionDatabaseException => exception.Message,
        _ => "an unexpected local error occurred",
    };

    private sealed record ActionComposition(
        LocalPathPolicy PathPolicy,
        SessionActionRouter Router,
        ClaudeLiveActivityScanner ClaudeActivityScanner,
        CodexLiveActivityDiscovery CodexActivityDiscovery,
        ResolvedExecutable? ClaudeExecutable,
        ResolvedExecutable? CodexExecutable,
        ResolvedExecutable? WindowsTerminal);

    private sealed record ActivityContext(
        ClaudeLiveActivitySnapshot? ClaudeSnapshot,
        IReadOnlyDictionary<SessionIdentity, IReadOnlyList<Guid>> CodexChildren);

    private sealed record ResultListState(
        IReadOnlyList<SessionIdentity> SelectedIdentities,
        SessionIdentity? FocusedIdentity,
        SessionIdentity? TopIdentity,
        int TopIndex)
    {
        public static ResultListState Empty { get; } = new([], null, null, 0);
    }

    private sealed record SearchGeneration(
        int Id,
        ParsedQuery Query,
        SearchScope Scope,
        string? DirectoryFilter)
    {
        public SessionSearchRequest CreateRequest(
            int pageNumber,
            SearchContentMode contentMode) => new(
                Query,
                Scope,
                pageNumber,
                50,
                DirectoryFilter,
                contentMode);
    }
}
