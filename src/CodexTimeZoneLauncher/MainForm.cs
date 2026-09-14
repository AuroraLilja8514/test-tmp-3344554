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
    private readonly CheckBox _proxyEnabled = new() { Text = "Route Codex through a proxy", AutoSize = true };
    private readonly ComboBox _proxyMode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly TextBox _proxyHost = new() { Dock = DockStyle.Fill };
    private readonly TextBox _proxyPort = new() { Dock = DockStyle.Fill };
    private readonly TextBox _proxyUsername = new() { Dock = DockStyle.Fill };
    private readonly TextBox _proxyPassword = new() { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
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
        Text = "Codex Time Zone & Proxy Launcher";
        MinimumSize = new Size(780, 650);
        StartPosition = FormStartPosition.CenterScreen;

        foreach (var zone in TimeZoneResolver.GetIanaCatalog()) _timezoneBox.Items.Add(zone);
        _timezoneBox.Text = _settings.TimeZone;

        _proxyMode.Items.AddRange(new object[] { "HTTP", "HTTPS", "SOCKS5", "SOCKS5h" });
        _proxyEnabled.Checked = _settings.ProxyEnabled;
        _proxyMode.SelectedItem = _proxyMode.Items.Cast<object>().FirstOrDefault(x =>
            string.Equals(x.ToString(), _settings.ProxyMode, StringComparison.OrdinalIgnoreCase)) ?? "HTTP";
        _proxyHost.Text = _settings.ProxyHost;
        _proxyPort.Text = _settings.ProxyPort is >= 1 and <= 65535 ? _settings.ProxyPort.ToString() : "8080";
        _proxyUsername.Text = _settings.ProxyUsername;
        _proxyPassword.Text = string.Empty;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 2,
            RowCount = 6,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
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
            Text = "IANA IDs are fully supported. Fixed UTC offsets are recognized; non-zero fixed offsets still need native fixed-offset rules.",
            AutoSize = true,
            MaximumSize = new Size(650, 0),
        }, 1, 2);

        var proxyGroup = BuildProxyGroup();
        layout.Controls.Add(proxyGroup, 0, 3);
        layout.SetColumnSpan(proxyGroup, 2);
        layout.Controls.Add(_launchButton, 1, 4);
        layout.Controls.Add(_logBox, 0, 5);
        layout.SetColumnSpan(_logBox, 2);
        Controls.Add(layout);

        _proxyEnabled.CheckedChanged += (_, _) => UpdateProxyControls();
        UpdateProxyControls();
        _launchButton.Click += async (_, _) => await LaunchAsync();
        Shown += async (_, _) => await DiscoverCodexAsync();
        FormClosing += OnFormClosing;
        FormClosed += async (_, _) =>
        {
            _lifetime.Cancel();
            await _coordinator.DisposeAsync();
            _lifetime.Dispose();
        };
    }

    private Control BuildProxyGroup()
    {
        var group = new GroupBox
        {
            Text = "Process-scoped network route",
            Dock = DockStyle.Top,
            AutoSize = true,
            Padding = new Padding(10),
        };
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 7,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.Controls.Add(_proxyEnabled, 0, 0);
        table.SetColumnSpan(_proxyEnabled, 2);
        table.Controls.Add(new Label { Text = "Type", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        table.Controls.Add(_proxyMode, 1, 1);
        table.Controls.Add(new Label { Text = "Host", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        table.Controls.Add(_proxyHost, 1, 2);
        table.Controls.Add(new Label { Text = "Port", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        table.Controls.Add(_proxyPort, 1, 3);
        table.Controls.Add(new Label { Text = "Username", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 4);
        table.Controls.Add(_proxyUsername, 1, 4);
        table.Controls.Add(new Label { Text = "Password", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 5);
        table.Controls.Add(_proxyPassword, 1, 5);
        var note = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            Text = "HTTP, HTTPS, SOCKS5 and SOCKS5h are normalized through a private 127.0.0.1 HTTP CONNECT adapter. Windows proxy settings are not changed. Passwords are session-only and are never saved to settings.json.",
        };
        table.Controls.Add(note, 0, 6);
        table.SetColumnSpan(note, 2);
        group.Controls.Add(table);
        return group;
    }

    private void UpdateProxyControls()
    {
        var enabled = _proxyEnabled.Checked;
        _proxyMode.Enabled = enabled;
        _proxyHost.Enabled = enabled;
        _proxyPort.Enabled = enabled;
        _proxyUsername.Enabled = enabled;
        _proxyPassword.Enabled = enabled;
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
            var proxy = ProxyConfigurationParser.Resolve(
                _proxyEnabled.Checked,
                _proxyMode.Text,
                _proxyHost.Text,
                _proxyPort.Text,
                _proxyUsername.Text,
                _proxyPassword.Text);

            _timezoneBox.Text = timezone.IanaId;
            _settings.TimeZone = timezone.IanaId;
            _settings.ProxyEnabled = _proxyEnabled.Checked;
            _settings.ProxyMode = _proxyMode.Text;
            _settings.ProxyHost = _proxyHost.Text.Trim();
            _settings.ProxyPort = int.TryParse(_proxyPort.Text, out var savedPort) ? savedPort : 0;
            _settings.ProxyUsername = _proxyUsername.Text;
            _settingsStore.Save(_settings);

            AppendLog("");
            AppendLog("Starting launch...");
            var result = await _coordinator.LaunchAsync(_installation, timezone, proxy, AppendLogThreadSafe, _lifetime.Token);
            AppendLog($"PASS: Chromium uses {result.TimeZone.IanaId}; native broker log: {result.ShimLogPath}");
            if (result.LocalProxyPort is int localPort)
                AppendLog($"PASS: process-scoped proxy adapter is active on 127.0.0.1:{localPort}.");
            _launchButton.Text = "Codex launched";
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            AppendLog("Launch cancelled. The bootstrap attempts to resume Codex before disconnecting.");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Codex Time Zone & Proxy Launcher", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _launchButton.Enabled = true;
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_coordinator.HasActiveProxy || e.CloseReason != CloseReason.UserClosing) return;
        var answer = MessageBox.Show(
            this,
            "The process-scoped proxy adapter is still serving the running Codex process. Closing this launcher will stop that adapter and Codex network requests routed through it will fail.\n\nClose the launcher anyway?",
            "Proxy is still active",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);
        if (answer != DialogResult.Yes) e.Cancel = true;
    }

    private void AppendLogThreadSafe(string message)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(new Action(() => AppendLog(message)));
        else AppendLog(message);
    }

    private void AppendLog(string message)
    {
        _logBox.AppendText(message + Environment.NewLine);
    }
}
