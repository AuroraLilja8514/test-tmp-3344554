using CodexTimeZoneLauncher.Models;
using CodexTimeZoneLauncher.Services;

namespace CodexTimeZoneLauncher;

public sealed class MainForm : Form
{
    private readonly SettingsStore _settingsStore = new();
    private readonly LauncherSettings _settings;
    private readonly AppxCodexLocator _locator = new();
    private readonly LaunchCoordinator _coordinator = new();
    private readonly CancellationTokenSource _lifetime = new();

    private readonly Label _packageLabel = new() { AutoSize = true, Text = "Finding OpenAI.Codex..." };
    private readonly ComboBox _timezoneBox = new()
    {
        Dock = DockStyle.Fill,
        DropDownStyle = ComboBoxStyle.DropDown,
        AutoCompleteMode = AutoCompleteMode.SuggestAppend,
        AutoCompleteSource = AutoCompleteSource.ListItems,
    };
    private readonly Button _launchButton = new() { Text = "Launch Codex", AutoSize = true, Enabled = false };
    private readonly TextBox _logBox = new()
    {
        Dock = DockStyle.Fill,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Font = new Font(FontFamily.GenericMonospace, 9f),
    };

    private CodexInstallation? _installation;

    public MainForm()
    {
        _settings = _settingsStore.LoadOnce();
        Text = "Codex Time Zone Launcher";
        MinimumSize = new Size(720, 500);
        StartPosition = FormStartPosition.CenterScreen;

        foreach (var zone in TimeZoneResolver.GetIanaCatalog()) _timezoneBox.Items.Add(zone);
        _timezoneBox.Text = _settings.TimeZone;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 5,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label { Text = "Codex package", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_packageLabel, 1, 0);
        layout.Controls.Add(new Label { Text = "Timezone", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        layout.Controls.Add(_timezoneBox, 1, 1);
        layout.Controls.Add(new Label
        {
            Text = "IANA IDs are fully supported. Fixed UTC offsets are recognized; non-zero fixed offsets need the remaining native fixed-offset implementation.",
            AutoSize = true,
            MaximumSize = new Size(620, 0),
        }, 1, 2);
        layout.Controls.Add(_launchButton, 1, 3);
        layout.Controls.Add(_logBox, 0, 4);
        layout.SetColumnSpan(_logBox, 2);
        Controls.Add(layout);

        _launchButton.Click += async (_, _) => await LaunchAsync();
        Shown += async (_, _) => await DiscoverCodexAsync();
        FormClosed += (_, _) => _lifetime.Cancel();
    }

    private async Task DiscoverCodexAsync()
    {
        try
        {
            _installation = await _locator.LocateAsync(_lifetime.Token);
            _packageLabel.Text = $"{_installation.Version}  —  {_installation.ExecutablePath}";
            AppendLog($"Found {_installation.PackageFullName}");
            _launchButton.Enabled = true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _packageLabel.Text = "Codex not available";
            AppendLog("ERROR: " + ex.Message);
        }
    }

    private async Task LaunchAsync()
    {
        if (_installation is null) return;
        _launchButton.Enabled = false;
        try
        {
            var timezone = TimeZoneResolver.Resolve(_timezoneBox.Text);
            _timezoneBox.Text = timezone.IanaId;
            _settings.TimeZone = timezone.IanaId;
            _settingsStore.Save(_settings);

            AppendLog("");
            AppendLog("Starting launch...");
            var result = await _coordinator.LaunchAsync(_installation, timezone, AppendLogThreadSafe, _lifetime.Token);
            AppendLog($"PASS: Chromium uses {result.TimeZone.IanaId}; native broker log: {result.ShimLogPath}");
            _launchButton.Text = "Codex launched";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            AppendLog("Launch cancelled. The bootstrap attempts to resume Codex before disconnecting.");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Codex Time Zone Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _launchButton.Enabled = true;
        }
    }

    private void AppendLogThreadSafe(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(() => AppendLog(message));
        else AppendLog(message);
    }

    private void AppendLog(string message)
    {
        _logBox.AppendText(message + Environment.NewLine);
    }
}
