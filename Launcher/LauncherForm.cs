using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Tractus.HtmlToNdi.Launcher;

/// <summary>
/// Represents the main form for the launcher application.
/// </summary>
public sealed class LauncherForm : Form
{
    private readonly TextBox _ndiNameTextBox;
    private readonly NumericUpDown _portNumericUpDown;
    private readonly TextBox _urlTextBox;
    private readonly NumericUpDown _widthNumericUpDown;
    private readonly NumericUpDown _heightNumericUpDown;
    private readonly TextBox _frameRateTextBox;
    private readonly CheckBox _enableBufferingCheckBox;
    private readonly NumericUpDown _bufferDepthNumericUpDown;
    private readonly CheckBox _allowLatencyExpansionCheckBox;
    private readonly NumericUpDown _telemetryNumericUpDown;
    private readonly Label _clockPrecisionLabel;
    private readonly TextBox _windowlessFrameRateTextBox;
    private readonly CheckBox _disableGpuVsyncCheckBox;
    private readonly CheckBox _disableFrameRateLimitCheckBox;
    private readonly CheckBox _alignWithCaptureTimestampsCheckBox;
    private readonly CheckBox _enableCadenceTelemetryCheckBox;
    private readonly CheckBox _enablePacedInvalidationCheckBox;
    private readonly CheckBox _disablePacedInvalidationCheckBox;
    private readonly CheckBox _enableCaptureBackpressureCheckBox;
    private readonly CheckBox _enablePumpCadenceAdaptationCheckBox;
    private readonly CheckBox _smoothnessPumpAtWindowlessRateCheckBox;
    private readonly CheckBox _enableCompositorCaptureCheckBox;
    private readonly CheckBox _enableGpuRasterizationCheckBox;
    private readonly CheckBox _enableZeroCopyCheckBox;
    private readonly CheckBox _enableOutOfProcessRasterizationCheckBox;
    private readonly CheckBox _disableBackgroundThrottlingCheckBox;
    private readonly CheckBox _presetHighPerformanceCheckBox;
    private readonly CheckBox _forceHighPerformanceGpuCheckBox;
    private readonly CheckBox _enableBeginFrameSchedulingCheckBox;
    private readonly CheckBox _disableNewContentRenderingDelayCheckBox;
    private readonly TextBox _cefExtraArgsTextBox;
    private readonly ComboBox _pacingModeComboBox;
    private readonly CheckBox _ndiSendAsyncCheckBox;
    private readonly CheckBox _enablePreviewCheckBox;
    private bool _suppressPacingCheckboxUpdates;
    private bool _suppressPacingModeUpdates;

    /// <summary>
    /// Gets the selected launch parameters.
    /// </summary>
    public LaunchParameters? SelectedParameters { get; private set; }

    /// <summary>
    /// Gets the current launcher settings.
    /// </summary>
    public LauncherSettings? CurrentSettings { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="LauncherForm"/> class.
    /// </summary>
    /// <param name="initialSettings">The initial settings to populate the form with.</param>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="initialSettings"/> is null.</exception>
    public LauncherForm(LauncherSettings initialSettings)
    {
        if (initialSettings is null)
        {
            throw new ArgumentNullException(nameof(initialSettings));
        }

        Text = "NDI-Browser Launcher";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var mainLayout = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 3,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0),
        };
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(mainLayout);

        // General settings table
        var generalTable = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 0,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 0, 0, 8),
        };
        generalTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
        generalTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));

        _ndiNameTextBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(generalTable, "NDI Source Name", _ndiNameTextBox);

        _urlTextBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(generalTable, "Startup URL", _urlTextBox);

        _portNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 65535,
            Dock = DockStyle.Fill,
            Increment = 1,
        };
        AddRow(generalTable, "HTTP Port", _portNumericUpDown);

        _widthNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 10000,
            Dock = DockStyle.Fill,
            Increment = 1,
        };
        AddRow(generalTable, "Width", _widthNumericUpDown);

        _heightNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 10000,
            Dock = DockStyle.Fill,
            Increment = 1,
        };
        AddRow(generalTable, "Height", _heightNumericUpDown);

        _frameRateTextBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(generalTable, "Frame Rate (fps)", _frameRateTextBox);

        _enablePreviewCheckBox = new CheckBox
        {
            Text = "Show interactive preview window",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(generalTable, "Preview Window", _enablePreviewCheckBox);

        mainLayout.Controls.Add(generalTable, 0, 0);

        // TabControl for advanced options
        var tabControl = new TabControl
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 4, 0, 8),
        };

        // Tab: Pacing & Buffering
        var pacingTabPage = new TabPage("Pacing & Buffering")
        {
            UseVisualStyleBackColor = true,
            Padding = new Padding(8),
            AutoScroll = true,
        };
        var pacingTable = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 0,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        pacingTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        pacingTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        pacingTabPage.Controls.Add(pacingTable);

        _pacingModeComboBox = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList
        };
        _pacingModeComboBox.Items.AddRange(Enum.GetNames(typeof(PacingMode)));
        AddRow(pacingTable, "Pacing Mode", _pacingModeComboBox);

        _enableBufferingCheckBox = new CheckBox
        {
            Text = "Enable paced output buffer",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(pacingTable, "Output Buffer", _enableBufferingCheckBox);

        _bufferDepthNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 3000,
            Dock = DockStyle.Fill,
            Increment = 1,
        };
        AddRow(pacingTable, "Buffer Depth (frames)", _bufferDepthNumericUpDown);

        _allowLatencyExpansionCheckBox = new CheckBox
        {
            Text = "Play queued frames during recovery (variable latency)",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(pacingTable, "Latency Expansion", _allowLatencyExpansionCheckBox);

        _telemetryNumericUpDown = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 3600,
            DecimalPlaces = 1,
            Increment = 0.5M,
            Dock = DockStyle.Fill,
        };
        AddRow(pacingTable, "Telemetry Interval (s)", _telemetryNumericUpDown);

        var stopwatchPrecisionNs = 1_000_000_000d / Stopwatch.Frequency;
        var clockPrecisionText = System.FormattableString.Invariant(
            $"{stopwatchPrecisionNs:F3} ns ({(Stopwatch.IsHighResolution ? "high" : "standard")} resolution clock)");
        _clockPrecisionLabel = new Label
        {
            Text = clockPrecisionText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = true,
            Padding = new Padding(0, 4, 0, 4)
        };
        AddRow(pacingTable, "Clock Precision", _clockPrecisionLabel);

        _alignWithCaptureTimestampsCheckBox = new CheckBox
        {
            Text = "Align paced output to capture timestamps",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(pacingTable, "Capture Alignment", _alignWithCaptureTimestampsCheckBox);

        _enableCadenceTelemetryCheckBox = new CheckBox
        {
            Text = "Log capture/output cadence metrics",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(pacingTable, "Cadence Telemetry", _enableCadenceTelemetryCheckBox);

        _enablePacedInvalidationCheckBox = new CheckBox
        {
            Text = "Request Chromium frames from paced sender",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(pacingTable, "Paced Invalidation", _enablePacedInvalidationCheckBox);

        _disablePacedInvalidationCheckBox = new CheckBox
        {
            Text = "Force periodic Chromium invalidation",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(pacingTable, "Disable Pacing", _disablePacedInvalidationCheckBox);

        _enableCaptureBackpressureCheckBox = new CheckBox
        {
            Text = "Pause Chromium when buffer is ahead",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(pacingTable, "Capture Backpressure", _enableCaptureBackpressureCheckBox);

        _enablePumpCadenceAdaptationCheckBox = new CheckBox
        {
            Text = "Adapt Chromium pump using cadence telemetry",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(pacingTable, "Pump Cadence Adaptation", _enablePumpCadenceAdaptationCheckBox);

        _smoothnessPumpAtWindowlessRateCheckBox = new CheckBox
        {
            Text = "Drive Smoothness pump from windowless render rate",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(pacingTable, "Smoothness Pump Cadence", _smoothnessPumpAtWindowlessRateCheckBox);

        _enableCompositorCaptureCheckBox = new CheckBox
        {
            Text = "Capture frames from Chromium compositor (experimental)",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(pacingTable, "Compositor Capture (Experimental)", _enableCompositorCaptureCheckBox);

        _windowlessFrameRateTextBox = new TextBox { Dock = DockStyle.Fill };
        AddRow(pacingTable, "Windowless Frame Rate", _windowlessFrameRateTextBox);

        _ndiSendAsyncCheckBox = new CheckBox
        {
            Text = "Use asynchronous NDI sending",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(pacingTable, "Asynchronous Sending", _ndiSendAsyncCheckBox);

        tabControl.TabPages.Add(pacingTabPage);

        // Tab: Performance & CEF
        var perfTabPage = new TabPage("Performance & CEF")
        {
            UseVisualStyleBackColor = true,
            Padding = new Padding(8),
            AutoScroll = true,
        };
        var perfTable = new TableLayoutPanel
        {
            ColumnCount = 2,
            RowCount = 0,
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        perfTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        perfTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        perfTabPage.Controls.Add(perfTable);

        _presetHighPerformanceCheckBox = new CheckBox
        {
            Text = "High-Performance Preset",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(perfTable, "Preset", _presetHighPerformanceCheckBox);

        _enableGpuRasterizationCheckBox = new CheckBox
        {
            Text = "Enable GPU rasterization",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(perfTable, "GPU Rasterization", _enableGpuRasterizationCheckBox);

        _enableZeroCopyCheckBox = new CheckBox
        {
            Text = "Enable zero-copy raster uploads",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(perfTable, "Zero-Copy", _enableZeroCopyCheckBox);

        _enableOutOfProcessRasterizationCheckBox = new CheckBox
        {
            Text = "Enable out-of-process rasterizer",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(perfTable, "OOP Rasterizer", _enableOutOfProcessRasterizationCheckBox);

        _disableGpuVsyncCheckBox = new CheckBox
        {
            Text = "Disable GPU VSync",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(perfTable, "GPU VSync", _disableGpuVsyncCheckBox);

        _disableFrameRateLimitCheckBox = new CheckBox
        {
            Text = "Disable frame rate limit",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(perfTable, "Frame Rate Limit", _disableFrameRateLimitCheckBox);

        _disableBackgroundThrottlingCheckBox = new CheckBox
        {
            Text = "Disable background throttling",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(perfTable, "Background Throttling", _disableBackgroundThrottlingCheckBox);

        _forceHighPerformanceGpuCheckBox = new CheckBox
        {
            Text = "Force high-performance GPU",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(perfTable, "Force Dedicated GPU", _forceHighPerformanceGpuCheckBox);

        _enableBeginFrameSchedulingCheckBox = new CheckBox
        {
            Text = "Enable begin-frame scheduling",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(perfTable, "Begin-Frame Scheduling", _enableBeginFrameSchedulingCheckBox);

        _disableNewContentRenderingDelayCheckBox = new CheckBox
        {
            Text = "Disable new content rendering delay",
            Dock = DockStyle.Fill,
            AutoSize = true,
        };
        AddRow(perfTable, "Rendering Delay", _disableNewContentRenderingDelayCheckBox);

        _cefExtraArgsTextBox = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "flag1=val1;flag2=val2"
        };
        AddRow(perfTable, "CEF Extra Args", _cefExtraArgsTextBox);

        tabControl.TabPages.Add(perfTabPage);

        mainLayout.Controls.Add(tabControl, 0, 1);

        var buttonPanel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };

        var launchButton = new Button
        {
            Text = "Launch",
            AutoSize = true,
            Padding = new Padding(12, 6, 12, 6)
        };
        launchButton.Click += (_, _) => OnLaunch();

        var cancelButton = new Button
        {
            Text = "Cancel",
            DialogResult = DialogResult.Cancel,
            AutoSize = true,
            Padding = new Padding(12, 6, 12, 6)
        };

        buttonPanel.Controls.Add(launchButton);
        buttonPanel.Controls.Add(cancelButton);

        mainLayout.Controls.Add(buttonPanel, 0, 2);

        AcceptButton = launchButton;
        CancelButton = cancelButton;

        _enableBufferingCheckBox.CheckedChanged += (_, _) => UpdateBufferingDependentControls();
        _enablePacedInvalidationCheckBox.CheckedChanged += OnEnablePacedInvalidationCheckedChanged;
        _disablePacedInvalidationCheckBox.CheckedChanged += OnDisablePacedInvalidationCheckedChanged;
        _presetHighPerformanceCheckBox.CheckedChanged += OnPresetHighPerformanceCheckedChanged;
        _pacingModeComboBox.SelectedIndexChanged += OnPacingModeChanged;

        ApplySettings(initialSettings);
    }

    private void ApplySettings(LauncherSettings settings)
    {
        _ndiNameTextBox.Text = settings.NdiName;
        _portNumericUpDown.Value = Math.Clamp(settings.Port, (int)_portNumericUpDown.Minimum, (int)_portNumericUpDown.Maximum);
        _urlTextBox.Text = settings.Url;
        _widthNumericUpDown.Value = Math.Clamp(settings.Width, (int)_widthNumericUpDown.Minimum, (int)_widthNumericUpDown.Maximum);
        _heightNumericUpDown.Value = Math.Clamp(settings.Height, (int)_heightNumericUpDown.Minimum, (int)_heightNumericUpDown.Maximum);
        _frameRateTextBox.Text = settings.FrameRate;
        _enableBufferingCheckBox.Checked = settings.EnableBuffering;
        _bufferDepthNumericUpDown.Value = Math.Clamp(
            settings.BufferDepth <= 0 ? 1 : settings.BufferDepth,
            (int)_bufferDepthNumericUpDown.Minimum,
            (int)_bufferDepthNumericUpDown.Maximum);
        _bufferDepthNumericUpDown.Enabled = settings.EnableBuffering;
        _allowLatencyExpansionCheckBox.Checked = settings.AllowLatencyExpansion;
        _allowLatencyExpansionCheckBox.Enabled = settings.EnableBuffering;
        var telemetryValue = (decimal)Math.Clamp(settings.TelemetryIntervalSeconds, (double)_telemetryNumericUpDown.Minimum, (double)_telemetryNumericUpDown.Maximum);
        _telemetryNumericUpDown.Value = telemetryValue;
        _alignWithCaptureTimestampsCheckBox.Checked = settings.AlignWithCaptureTimestamps;
        _enableCadenceTelemetryCheckBox.Checked = settings.EnableCadenceTelemetry;
        _suppressPacingCheckboxUpdates = true;
        _enablePacedInvalidationCheckBox.Checked = settings.EnablePacedInvalidation;
        _disablePacedInvalidationCheckBox.Checked = settings.DisablePacedInvalidation;
        _suppressPacingCheckboxUpdates = false;
        _enableCaptureBackpressureCheckBox.Checked = settings.EnableCaptureBackpressure;
        _enablePumpCadenceAdaptationCheckBox.Checked = settings.EnablePumpCadenceAdaptation;
        _smoothnessPumpAtWindowlessRateCheckBox.Checked = settings.SmoothnessPumpAtWindowlessRate;
        _enableCompositorCaptureCheckBox.Checked = settings.EnableCompositorCapture;
        _windowlessFrameRateTextBox.Text = settings.WindowlessFrameRateOverride ?? string.Empty;
        _disableGpuVsyncCheckBox.Checked = settings.DisableGpuVsync;
        _disableFrameRateLimitCheckBox.Checked = settings.DisableFrameRateLimit;
        _enableGpuRasterizationCheckBox.Checked = settings.EnableGpuRasterization;
        _enableZeroCopyCheckBox.Checked = settings.EnableZeroCopy;
        _enableOutOfProcessRasterizationCheckBox.Checked = settings.EnableOutOfProcessRasterization;
        _disableBackgroundThrottlingCheckBox.Checked = settings.DisableBackgroundThrottling;
        _presetHighPerformanceCheckBox.Checked = settings.PresetHighPerformance;
        _forceHighPerformanceGpuCheckBox.Checked = settings.ForceHighPerformanceGpu;
        _enableBeginFrameSchedulingCheckBox.Checked = settings.EnableBeginFrameScheduling;
        _disableNewContentRenderingDelayCheckBox.Checked = settings.DisableNewContentRenderingDelay;
        _cefExtraArgsTextBox.Text = settings.CefExtraArgs ?? string.Empty;
        _suppressPacingModeUpdates = true;
        _pacingModeComboBox.SelectedItem = settings.PacingMode.ToString();
        if (_pacingModeComboBox.SelectedIndex < 0 && _pacingModeComboBox.Items.Count > 0)
        {
            _pacingModeComboBox.SelectedIndex = 0;
        }
        _suppressPacingModeUpdates = false;
        _ndiSendAsyncCheckBox.Checked = settings.NdiSendAsync;
        _enablePreviewCheckBox.Checked = settings.EnablePreview;

        UpdateBufferingDependentControls();
        UpdateHighPerformancePresetControls();
    }

    private void OnPresetHighPerformanceCheckedChanged(object? sender, EventArgs e)
    {
        UpdateHighPerformancePresetControls();
    }

    private void UpdateHighPerformancePresetControls()
    {
        var presetEnabled = _presetHighPerformanceCheckBox.Checked;

        _enableGpuRasterizationCheckBox.Enabled = !presetEnabled;
        _enableZeroCopyCheckBox.Enabled = !presetEnabled;
        _enableOutOfProcessRasterizationCheckBox.Enabled = !presetEnabled;
        _disableGpuVsyncCheckBox.Enabled = !presetEnabled;
        _disableFrameRateLimitCheckBox.Enabled = !presetEnabled;
        _disableBackgroundThrottlingCheckBox.Enabled = !presetEnabled;

        if (presetEnabled)
        {
            _enableGpuRasterizationCheckBox.Checked = true;
            _enableZeroCopyCheckBox.Checked = true;
            _enableOutOfProcessRasterizationCheckBox.Checked = true;
            _disableGpuVsyncCheckBox.Checked = true;
            _disableFrameRateLimitCheckBox.Checked = true;
            _disableBackgroundThrottlingCheckBox.Checked = true;
        }
    }

    private void OnPacingModeChanged(object? sender, EventArgs e)
    {
        if (_pacingModeComboBox.SelectedItem is "Smoothness")
        {
            if (_suppressPacingModeUpdates)
            {
                return;
            }

            if (!_enableBufferingCheckBox.Checked)
            {
                _enableBufferingCheckBox.Checked = true;
            }

            // Manual user selection should always reapply the Smoothness default depth; suppressed
            // programmatic updates (e.g., loading persisted settings) bypass this path so saved
            // values are preserved.
            _bufferDepthNumericUpDown.Value = LaunchParameters.SmoothnessDefaultBufferDepth;
        }
    }

    private void UpdateBufferingDependentControls()
    {
        var bufferingEnabled = _enableBufferingCheckBox.Checked;
        _bufferDepthNumericUpDown.Enabled = bufferingEnabled;
        _allowLatencyExpansionCheckBox.Enabled = bufferingEnabled;
        _enablePacedInvalidationCheckBox.Enabled = true;
        _disablePacedInvalidationCheckBox.Enabled = true;
        _enablePumpCadenceAdaptationCheckBox.Enabled = true;

        var backpressureAllowed = bufferingEnabled
            && _enablePacedInvalidationCheckBox.Checked
            && !_disablePacedInvalidationCheckBox.Checked;
        _enableCaptureBackpressureCheckBox.Enabled = backpressureAllowed;
        if (!backpressureAllowed)
        {
            _enableCaptureBackpressureCheckBox.Checked = false;
        }
    }

    private void OnEnablePacedInvalidationCheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressPacingCheckboxUpdates)
        {
            return;
        }

        if (_enablePacedInvalidationCheckBox.Checked)
        {
            _suppressPacingCheckboxUpdates = true;
            _disablePacedInvalidationCheckBox.Checked = false;
            _suppressPacingCheckboxUpdates = false;
        }

        UpdateBufferingDependentControls();
    }

    private void OnDisablePacedInvalidationCheckedChanged(object? sender, EventArgs e)
    {
        if (_suppressPacingCheckboxUpdates)
        {
            return;
        }

        if (_disablePacedInvalidationCheckBox.Checked)
        {
            _suppressPacingCheckboxUpdates = true;
            _enablePacedInvalidationCheckBox.Checked = false;
            _enableCaptureBackpressureCheckBox.Checked = false;
            _suppressPacingCheckboxUpdates = false;
        }

        UpdateBufferingDependentControls();
    }

    private static void AddSectionHeader(TableLayoutPanel table, string text)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var header = new Label
        {
            Text = text,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = true,
            Padding = new Padding(0, 12, 0, 4),
            Font = new Font(table.Font, FontStyle.Bold)
        };

        table.Controls.Add(header, 0, table.RowCount);
        table.SetColumnSpan(header, 2);
        table.RowCount++;
    }

    private void OnLaunch()
    {
        var ndiName = _ndiNameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(ndiName))
        {
            MessageBox.Show(this, "Please enter an NDI source name.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _ndiNameTextBox.Focus();
            return;
        }

        var url = _urlTextBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            MessageBox.Show(this, "Please enter a valid absolute URL.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _urlTextBox.Focus();
            return;
        }

        var frameRateText = _frameRateTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(frameRateText))
        {
            frameRateText = "60";
        }

        var pacingModeSelection = _pacingModeComboBox.SelectedItem as string;
        if (string.IsNullOrWhiteSpace(pacingModeSelection))
        {
            MessageBox.Show(this, "Please choose a pacing mode.", "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _pacingModeComboBox.Focus();
            return;
        }

        var settings = new LauncherSettings
        {
            NdiName = ndiName,
            Port = (int)_portNumericUpDown.Value,
            Url = url,
            Width = (int)_widthNumericUpDown.Value,
            Height = (int)_heightNumericUpDown.Value,
            FrameRate = frameRateText,
            EnableBuffering = _enableBufferingCheckBox.Checked,
            BufferDepth = (int)_bufferDepthNumericUpDown.Value,
            TelemetryIntervalSeconds = (double)_telemetryNumericUpDown.Value,
            AlignWithCaptureTimestamps = _alignWithCaptureTimestampsCheckBox.Checked,
            EnableCadenceTelemetry = _enableCadenceTelemetryCheckBox.Checked,
            WindowlessFrameRateOverride = string.IsNullOrWhiteSpace(_windowlessFrameRateTextBox.Text)
                ? null
                : _windowlessFrameRateTextBox.Text.Trim(),
            DisableGpuVsync = _disableGpuVsyncCheckBox.Checked,
            DisableFrameRateLimit = _disableFrameRateLimitCheckBox.Checked,
            AllowLatencyExpansion = _allowLatencyExpansionCheckBox.Checked,
            EnablePacedInvalidation = _enablePacedInvalidationCheckBox.Checked,
            DisablePacedInvalidation = _disablePacedInvalidationCheckBox.Checked,
            EnableCaptureBackpressure = _enableBufferingCheckBox.Checked && _enableCaptureBackpressureCheckBox.Checked,
            EnablePumpCadenceAdaptation = _enablePumpCadenceAdaptationCheckBox.Checked,
            SmoothnessPumpAtWindowlessRate = _smoothnessPumpAtWindowlessRateCheckBox.Checked,
            EnableCompositorCapture = _enableCompositorCaptureCheckBox.Checked,
            EnableGpuRasterization = _enableGpuRasterizationCheckBox.Checked,
            EnableZeroCopy = _enableZeroCopyCheckBox.Checked,
            EnableOutOfProcessRasterization = _enableOutOfProcessRasterizationCheckBox.Checked,
            DisableBackgroundThrottling = _disableBackgroundThrottlingCheckBox.Checked,
            PresetHighPerformance = _presetHighPerformanceCheckBox.Checked,
            ForceHighPerformanceGpu = _forceHighPerformanceGpuCheckBox.Checked,
            EnableBeginFrameScheduling = _enableBeginFrameSchedulingCheckBox.Checked,
            DisableNewContentRenderingDelay = _disableNewContentRenderingDelayCheckBox.Checked,
            CefExtraArgs = string.IsNullOrWhiteSpace(_cefExtraArgsTextBox.Text) ? null : _cefExtraArgsTextBox.Text.Trim(),
            PacingMode = Enum.Parse<PacingMode>(pacingModeSelection),
            NdiSendAsync = _ndiSendAsyncCheckBox.Checked,
            EnablePreview = _enablePreviewCheckBox.Checked
        };

        try
        {
            SelectedParameters = LaunchParameters.FromSettings(settings);
            CurrentSettings = settings;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (FormatException ex)
        {
            MessageBox.Show(this, ex.Message, "Validation", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static void AddRow(TableLayoutPanel table, string labelText, Control control)
    {
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var label = new Label
        {
            Text = labelText,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoSize = true,
            Padding = new Padding(0, 4, 8, 4)
        };

        table.Controls.Add(label, 0, table.RowCount);
        table.Controls.Add(control, 1, table.RowCount);
        table.RowCount++;
    }
}
