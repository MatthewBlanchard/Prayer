using Terminal.Gui;
using System;
using System.Collections.Generic;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Channels;

public class BotWindow
{
    private enum StateTab
    {
        Space,
        Trade,
        Shipyard,
        Cantina
    }

    private TextView _state;
    private ListView _scriptLinesList;
    private ListView _memory;
    private TextView _input;
    private Button _editScriptButton;
    private Button _useMissionPromptButton;
    private Button _saveExampleButton;
    private Button _executeButton;
    private CheckBox _loopCheckBox;
    private volatile bool _loopEnabled;

    private ChannelWriter<string>? _controlInputWriter;
    private ChannelWriter<string>? _generateScriptWriter;
    private ChannelWriter<bool>? _saveExampleWriter;
    private ChannelWriter<bool>? _executeScriptWriter;
    private ChannelWriter<string>? _switchBotWriter;
    private ChannelWriter<AddBotRequest>? _addBotWriter;
    private ChannelWriter<LlmProviderSelection>? _llmSelectionWriter;

    private Window _win;
    private View _playerFrame;
    private View _playerStack;
    private View _llmSelectorStack;
    private View _topInfoBar;
    private ComboBox _providerDropdown;
    private ComboBox _modelDropdown;
    private FrameView _executionStatusFrame;
    private ListView _executionStatusList;
    private Button _spaceTabButton;
    private Button _tradeTabButton;
    private Button _shipyardTabButton;
    private Button _cantinaTabButton;
    private StateTab _selectedStateTab = StateTab.Space;
    private FrameView _scriptFrame;
    private FrameView _memoryFrame;
    private FrameView _inputFrame;
    private ColorScheme _scriptSideScheme;
    private ColorScheme _activeBotScheme;
    private string? _currentControlInput;
    private string _lastSpaceStateMarkdown = "";
    private string? _lastTradeStateMarkdown;
    private string? _lastShipyardStateMarkdown;
    private string? _lastCantinaStateMarkdown;
    private string _lastRenderedStateMarkdown = "";
    private IReadOnlyList<MissionPromptOption> _activeMissionPrompts = Array.Empty<MissionPromptOption>();
    private readonly List<View> _playerControls = new();
    private string _lastPlayerSignature = "";
    private volatile bool _uiReady;
    private bool _suppressLlmSelectionEvents;
    private readonly object _llmModelsLock = new();
    private readonly List<string> _llmProviders = new();
    private string _initialLlmProvider = "openai";
    private string _initialLlmModel = "gpt-4o-mini";
    private readonly Dictionary<string, IReadOnlyList<string>> _llmModelsByProvider =
        new(StringComparer.OrdinalIgnoreCase);

    public bool LoopEnabled => _loopEnabled;

    public BotWindow()
    {
        _llmProviders.Add("llamacpp");
        _llmModelsByProvider["llamacpp"] = new List<string> { "model" };
    }

    // -----------------------------
    // Wiring
    // -----------------------------
    public void SetControlInputWriter(ChannelWriter<string> writer)
    {
        _controlInputWriter = writer;
    }

    public void SetGenerateScriptWriter(ChannelWriter<string> writer)
    {
        _generateScriptWriter = writer;
    }

    public void SetSaveExampleWriter(ChannelWriter<bool> writer)
    {
        _saveExampleWriter = writer;
    }

    public void SetExecuteScriptWriter(ChannelWriter<bool> writer)
    {
        _executeScriptWriter = writer;
    }

    public void SetSwitchBotWriter(ChannelWriter<string> writer)
    {
        _switchBotWriter = writer;
    }

    public void SetAddBotWriter(ChannelWriter<AddBotRequest> writer)
    {
        _addBotWriter = writer;
    }

    public void SetLlmSelectionWriter(ChannelWriter<LlmProviderSelection> writer)
    {
        _llmSelectionWriter = writer;
    }

    public void ConfigureInitialLlmSelection(string provider, string model)
    {
        if (!string.IsNullOrWhiteSpace(provider))
            _initialLlmProvider = provider.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(model))
            _initialLlmModel = model.Trim();
    }

    public void SetAvailableProviders(IReadOnlyList<string> providers)
    {
        var normalized = (providers ?? Array.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim().ToLowerInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
            normalized.Add("llamacpp");

        lock (_llmModelsLock)
        {
            _llmProviders.Clear();
            _llmProviders.AddRange(normalized);

            foreach (var provider in _llmProviders)
            {
                if (!_llmModelsByProvider.ContainsKey(provider))
                    _llmModelsByProvider[provider] = new List<string> { "model" };
            }
        }
    }

    public void SetProviderModels(string provider, IReadOnlyList<string> models)
    {
        var normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedProvider))
            return;

        var normalizedModels = (models ?? Array.Empty<string>())
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

        if (normalizedModels.Count == 0)
            return;

        lock (_llmModelsLock)
        {
            if (!_llmProviders.Contains(normalizedProvider, StringComparer.OrdinalIgnoreCase))
                _llmProviders.Add(normalizedProvider);
            _llmModelsByProvider[normalizedProvider] = normalizedModels;
        }

        if (!_uiReady || Application.MainLoop == null)
            return;

        Application.MainLoop.Invoke(() =>
        {
            var selectedProvider = GetSelectedProvider();
            if (!string.Equals(selectedProvider, normalizedProvider, StringComparison.OrdinalIgnoreCase))
                return;

            RefreshModelDropdownForSelectedProvider(publishSelection: false);
        });
    }

    // -----------------------------
    // UI Boot
    // -----------------------------
    public void Run()
    {
        Application.Init();

        // -----------------------------
        // Ctrl+C handling (CRITICAL)
        // -----------------------------
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Application.RequestStop();
        };

        // -----------------------------
        // Color schemes
        // -----------------------------
        var baseScheme = new ColorScheme
        {
            Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
            Focus = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black),
            HotNormal = Application.Driver.MakeAttribute(Color.Green, Color.Black),
            HotFocus = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black)
        };

        var stateScheme = new ColorScheme
        {
            Normal = Application.Driver.MakeAttribute(Color.Gray, Color.Black),
            Focus = Application.Driver.MakeAttribute(Color.White, Color.Black),
            HotNormal = Application.Driver.MakeAttribute(Color.Green, Color.Black),
            HotFocus = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black)
        };

        _scriptSideScheme = new ColorScheme
        {
            Normal = Application.Driver.MakeAttribute(Color.BrightYellow, Color.Black),
            Focus = Application.Driver.MakeAttribute(Color.White, Color.Black),
            HotNormal = Application.Driver.MakeAttribute(Color.BrightYellow, Color.Black),
            HotFocus = Application.Driver.MakeAttribute(Color.White, Color.Black)
        };

        _activeBotScheme = new ColorScheme
        {
            Normal = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black),
            Focus = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black),
            HotNormal = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black),
            HotFocus = Application.Driver.MakeAttribute(Color.BrightGreen, Color.Black)
        };

        // -----------------------------
        // Window
        // -----------------------------
        _win = new Window("Servator")
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ColorScheme = baseScheme
        };

        _playerFrame = new View
        {
            X = 0,
            Y = 0,
            Width = 18,
            Height = Dim.Fill(),
            ColorScheme = baseScheme
        };
        _playerStack = new View
        {
            X = 0,
            Y = 5,
            Width = Dim.Fill(),
            Height = Dim.Fill(5)
        };
        _llmSelectorStack = new View
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill(),
            Height = 5
        };
        _playerFrame.Add(_llmSelectorStack);
        _playerFrame.Add(_playerStack);

        _topInfoBar = new View
        {
            X = Pos.Right(_playerFrame),
            Y = 0,
            Width = Dim.Fill(48),
            Height = 1,
            ColorScheme = baseScheme
        };
        var providerLabel = new Label("Provider:")
        {
            X = 0,
            Y = 0,
            Width = Dim.Fill()
        };

        List<string> providerSnapshot;
        lock (_llmModelsLock)
            providerSnapshot = _llmProviders.ToList();

        _providerDropdown = new ComboBox(providerSnapshot)
        {
            X = 0,
            Y = 1,
            Width = Dim.Fill(),
            Height = 1,
            ReadOnly = true,
            Text = _initialLlmProvider
        };

        var modelLabel = new Label("Model:")
        {
            X = 0,
            Y = 2,
            Width = Dim.Fill()
        };

        _modelDropdown = new ComboBox(new List<string>())
        {
            X = 0,
            Y = 3,
            Width = Dim.Fill(),
            Height = 1,
            ReadOnly = true,
            Text = _initialLlmModel
        };

        _providerDropdown.SelectedItemChanged += _ =>
        {
            if (_suppressLlmSelectionEvents)
                return;

            RefreshModelDropdownForSelectedProvider(publishSelection: true);
        };
        _modelDropdown.SelectedItemChanged += _ =>
        {
            if (_suppressLlmSelectionEvents)
                return;

            PublishLlmSelection();
        };

        RefreshModelDropdownForSelectedProvider(publishSelection: true);

        _spaceTabButton = new Button("Space")
        {
            X = 0,
            Y = 0,
            Width = 9,
            Height = 1
        };
        _spaceTabButton.Clicked += () =>
        {
            _selectedStateTab = StateTab.Space;
            UpdateStateTabButtons(
                showTradeTab: _tradeTabButton.Visible,
                showShipyardTab: _shipyardTabButton.Visible,
                showCantinaTab: _cantinaTabButton.Visible);
            ApplySelectedStateTabText();
        };

        _tradeTabButton = new Button("Trade")
        {
            X = Pos.Right(_spaceTabButton) + 1,
            Y = 0,
            Width = 9,
            Height = 1,
            Visible = false
        };
        _tradeTabButton.Clicked += () =>
        {
            _selectedStateTab = StateTab.Trade;
            UpdateStateTabButtons(
                showTradeTab: true,
                showShipyardTab: _shipyardTabButton.Visible,
                showCantinaTab: _cantinaTabButton.Visible);
            ApplySelectedStateTabText();
        };

        _shipyardTabButton = new Button("Shipyard")
        {
            X = Pos.Right(_tradeTabButton) + 1,
            Y = 0,
            Width = 11,
            Height = 1,
            Visible = false
        };
        _shipyardTabButton.Clicked += () =>
        {
            _selectedStateTab = StateTab.Shipyard;
            UpdateStateTabButtons(
                showTradeTab: _tradeTabButton.Visible,
                showShipyardTab: true,
                showCantinaTab: _cantinaTabButton.Visible);
            ApplySelectedStateTabText();
        };

        _cantinaTabButton = new Button("Cantina")
        {
            X = Pos.Right(_shipyardTabButton) + 1,
            Y = 0,
            Width = 10,
            Height = 1,
            Visible = false
        };
        _cantinaTabButton.Clicked += () =>
        {
            _selectedStateTab = StateTab.Cantina;
            UpdateStateTabButtons(
                showTradeTab: _tradeTabButton.Visible,
                showShipyardTab: _shipyardTabButton.Visible,
                showCantinaTab: true);
            ApplySelectedStateTabText();
        };

        _llmSelectorStack.Add(
            providerLabel,
            _providerDropdown,
            modelLabel,
            _modelDropdown);

        _topInfoBar.Add(
            _spaceTabButton,
            _tradeTabButton,
            _shipyardTabButton,
            _cantinaTabButton);
        UpdateStateTabButtons(showTradeTab: false, showShipyardTab: false, showCantinaTab: false);

        RefreshPlayerStack(Array.Empty<BotTab>(), null);

        // -----------------------------
        // Layout (unchanged)
        // -----------------------------
        var stateFrame = new FrameView("State")
        {
            X = Pos.Right(_playerFrame),
            Y = 1,
            Width = Dim.Fill(48),
            Height = Dim.Fill(5),
            ColorScheme = stateScheme
        };

        _state = new TextView()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            ReadOnly = true
        };

        stateFrame.Add(_state);

        _executionStatusFrame = new FrameView("Execution")
        {
            X = Pos.Right(_playerFrame),
            Y = Pos.AnchorEnd(5),
            Width = Dim.Fill(48),
            Height = 5,
            ColorScheme = stateScheme
        };

        _executionStatusList = new ListView()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        _executionStatusFrame.Add(_executionStatusList);

        _scriptFrame = new FrameView("Script")
        {
            X = Pos.Right(stateFrame),
            Y = 0,
            Width = 48,
            Height = Dim.Percent(16),
            ColorScheme = _scriptSideScheme
        };

        _scriptLinesList = new ListView()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(2)
        };

        _loopCheckBox = new CheckBox("Loop")
        {
            X = 0,
            Y = Pos.AnchorEnd(2),
            Width = 12,
            Height = 1,
            Checked = false
        };
        _loopCheckBox.Toggled += _ => _loopEnabled = _loopCheckBox.Checked;

        _executeButton = new Button("Execute")
        {
            X = Pos.Right(_loopCheckBox) + 1,
            Y = Pos.AnchorEnd(2),
            Width = 11,
            Height = 1
        };
        _executeButton.Clicked += ExecuteScriptNow;

        _editScriptButton = new Button("Edit Script")
        {
            X = 0,
            Y = Pos.AnchorEnd(1),
            Width = 16,
            Height = 1
        };
        _editScriptButton.Clicked += OpenScriptEditor;

        _useMissionPromptButton = new Button("Mission -> Prompt")
        {
            X = Pos.Right(_editScriptButton) + 1,
            Y = Pos.AnchorEnd(1),
            Width = 18,
            Height = 1
        };
        _useMissionPromptButton.Clicked += OpenMissionPromptPicker;

        _saveExampleButton = new Button("Thumbs Up")
        {
            X = Pos.Right(_useMissionPromptButton) + 1,
            Y = Pos.AnchorEnd(1),
            Width = 12,
            Height = 1
        };
        _saveExampleButton.Clicked += SaveCurrentScriptAsExample;

        _scriptFrame.Add(_scriptLinesList);
        _scriptFrame.Add(_loopCheckBox);
        _scriptFrame.Add(_executeButton);
        _scriptFrame.Add(_editScriptButton);
        _scriptFrame.Add(_useMissionPromptButton);
        _scriptFrame.Add(_saveExampleButton);

        _memoryFrame = new FrameView("Memory")
        {
            X = Pos.Right(stateFrame),
            Y = Pos.Bottom(_scriptFrame),
            Width = 48,
            Height = Dim.Fill(5),
            ColorScheme = _scriptSideScheme
        };

        _memory = new ListView()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };

        _memoryFrame.Add(_memory);

        _inputFrame = new FrameView("Prompt")
        {
            X = Pos.Right(stateFrame),
            Y = Pos.AnchorEnd(5),
            Width = 48,
            Height = 5,
            ColorScheme = _scriptSideScheme
        };

        _input = new TextView()
        {
            Width = Dim.Fill(),
            Height = Dim.Fill()
        };
        _inputFrame.Add(_input);

        _input.KeyPress += args =>
        {
            if (args.KeyEvent.Key == Key.Enter ||
                args.KeyEvent.Key == (Key.CtrlMask | Key.S))
            {
                SubmitInput();
                args.Handled = true;
            }
        };

        _win.Add(_playerFrame, _topInfoBar, stateFrame, _executionStatusFrame, _memoryFrame, _scriptFrame, _inputFrame);
        Application.Top.Add(_win);

        ApplyScriptLayout();
        _uiReady = true;

        // -----------------------------
        // Exit keys
        // -----------------------------
        _win.KeyPress += args =>
        {
            if (args.KeyEvent.Key == Key.Esc ||
                args.KeyEvent.Key == (Key.CtrlMask | Key.C))
            {
                Application.RequestStop();
                args.Handled = true;
            }
        };

        try
        {
            Application.Run();
        }
        finally
        {
            // CRITICAL: ensures driver + mouse cleanup
            Application.Shutdown();
        }
    }

    private void RefreshModelDropdownForSelectedProvider(bool publishSelection)
    {
        string provider = GetSelectedProvider();
        string? preferredModel = _modelDropdown?.Text?.ToString()?.Trim();
        IReadOnlyList<string> models;
        lock (_llmModelsLock)
        {
            if (!_llmModelsByProvider.TryGetValue(provider, out models!) || models.Count == 0)
                models = new List<string> { "gpt-4o-mini" };
        }

        _suppressLlmSelectionEvents = true;
        _modelDropdown.SetSource(models.ToList());

        int selectedIndex = 0;
        if (!string.IsNullOrWhiteSpace(preferredModel))
        {
            var idx = models
                .Select((model, index) => (model, index))
                .FirstOrDefault(x => string.Equals(x.model, preferredModel, StringComparison.Ordinal))
                .index;
            if (idx >= 0 && idx < models.Count)
                selectedIndex = idx;
        }

        _modelDropdown.SelectedItem = selectedIndex;
        _modelDropdown.Text = models[selectedIndex];
        _suppressLlmSelectionEvents = false;

        if (publishSelection)
            PublishLlmSelection();
    }

    private string GetSelectedProvider()
    {
        var fromText = _providerDropdown.Text?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(fromText))
            return fromText.ToLowerInvariant();

        int idx = _providerDropdown.SelectedItem;
        lock (_llmModelsLock)
        {
            if (idx >= 0 && idx < _llmProviders.Count)
                return _llmProviders[idx];
            if (_llmProviders.Count > 0)
                return _llmProviders[0];
        }

        return "llamacpp";
    }

    private string GetSelectedModel()
    {
        var fromText = _modelDropdown.Text?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(fromText))
            return fromText;

        string provider = GetSelectedProvider();
        lock (_llmModelsLock)
        {
            if (_llmModelsByProvider.TryGetValue(provider, out var models) && models.Count > 0)
                return models[0];
        }

        return "gpt-4o-mini";
    }

    private void PublishLlmSelection()
    {
        _llmSelectionWriter?.TryWrite(new LlmProviderSelection(
            GetSelectedProvider(),
            GetSelectedModel()));
    }
    // -----------------------------
    // Render (thread-safe)
    // -----------------------------
    public void Render(
        string spaceStateMarkdown,
        string? tradeStateMarkdown,
        string? shipyardStateMarkdown,
        string? cantinaStateMarkdown,
        IReadOnlyList<MissionPromptOption> activeMissionPrompts,
        IReadOnlyList<string> memory,
        IReadOnlyList<string> executionStatusLines,
        string? controlInput,
        int? currentScriptLine,
        string? lastGenerationPrompt,
        IReadOnlyList<BotTab> bots,
        string? activeBotId)
    {
        if (!_uiReady || Application.MainLoop == null)
            return;

        Application.MainLoop.Invoke(() =>
        {
            ApplyScriptLayout();
            _currentControlInput = controlInput;
            RefreshPlayerStack(bots, activeBotId);
            bool showTradeTab = !string.IsNullOrWhiteSpace(tradeStateMarkdown);
            bool showShipyardTab = !string.IsNullOrWhiteSpace(shipyardStateMarkdown);
            bool showCantinaTab = !string.IsNullOrWhiteSpace(cantinaStateMarkdown);
            UpdateStateTabButtons(showTradeTab, showShipyardTab, showCantinaTab);
            _lastSpaceStateMarkdown = spaceStateMarkdown ?? "";
            _lastTradeStateMarkdown = tradeStateMarkdown;
            _lastShipyardStateMarkdown = shipyardStateMarkdown;
            _lastCantinaStateMarkdown = cantinaStateMarkdown;
            _activeMissionPrompts = activeMissionPrompts ?? Array.Empty<MissionPromptOption>();
            _useMissionPromptButton.Visible = _activeMissionPrompts.Count > 0;

            ApplySelectedStateTabText();

            var memoryDisplay = (memory ?? new List<string>())
                .Reverse()
                .ToList();
            SetListSourcePreserveScroll(_memory, memoryDisplay);

            var executionDisplay = (executionStatusLines ?? new List<string>())
                .ToList();
            if (executionDisplay.Count == 0)
                executionDisplay.Add("(no recent execution messages)");
            SetListSourcePreserveScroll(_executionStatusList, executionDisplay);
            if (string.IsNullOrWhiteSpace(controlInput))
            {
                SetListSourcePreserveScroll(_scriptLinesList, new List<string>
                {
                    "(no script loaded)"
                });
            }
            else
            {
                var objectiveLines = controlInput
                    .Replace("\r", "")
                    .Split('\n', StringSplitOptions.None)
                    .Select((line, idx) =>
                    {
                        var lineNo = idx + 1;
                        var marker = currentScriptLine.HasValue && currentScriptLine.Value == lineNo
                            ? ">"
                            : " ";
                        return $"{marker}{line}";
                    })
                    .ToList();

                SetListSourcePreserveScroll(_scriptLinesList, objectiveLines.Count > 0
                    ? objectiveLines
                    : new List<string> { controlInput.Trim() });
            }
        });
    }

    private void SubmitInput()
    {
        var text = _input.Text.ToString();
        if (string.IsNullOrWhiteSpace(text))
            return;

        _generateScriptWriter?.TryWrite(text);
        _input.Text = "";
        _input.SetFocus();
    }

    private void OpenScriptEditor()
    {
        string tempFile = Path.Combine(
            Path.GetTempPath(),
            $"spacemolt-script-{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllText(tempFile, _currentControlInput ?? "");

            string editor = Environment.GetEnvironmentVariable("VISUAL")
                ?? Environment.GetEnvironmentVariable("EDITOR")
                ?? "nano";
            string command = $"{editor} '{tempFile.Replace("'", "'\"'\"'")}'";

            var startInfo = new ProcessStartInfo("/bin/bash")
            {
                UseShellExecute = false
            };
            startInfo.ArgumentList.Add("-lc");
            startInfo.ArgumentList.Add(command);

            using var process = Process.Start(startInfo);
            process?.WaitForExit();

            if (process?.ExitCode == 0)
            {
                var edited = File.ReadAllText(tempFile);
                if (!string.IsNullOrWhiteSpace(edited))
                    _controlInputWriter?.TryWrite(edited);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(tempFile))
                    File.Delete(tempFile);
            }
            catch
            {
                // Temp file cleanup failures should not break UI.
            }

            Application.Refresh();
            _input.SetFocus();
        }
    }

    private void OpenAddBotModal()
    {
        var ok = new Button("Add");
        var cancel = new Button("Cancel");
        var dialog = new Dialog("Add Bot", 72, 17, ok, cancel);

        var modeLabel = new Label("Mode:")
        {
            X = 1,
            Y = 1,
            Width = 13
        };
        var modeRadio = new RadioGroup(
            new NStack.ustring[] { "_Login", "_Register" },
            0)
        {
            X = Pos.Right(modeLabel),
            Y = Pos.Top(modeLabel)
        };

        var userLabel = new Label("Username:")
        {
            X = 1,
            Y = Pos.Bottom(modeRadio) + 1,
            Width = 13
        };
        var userInput = new TextField("")
        {
            X = Pos.Right(userLabel),
            Y = Pos.Top(userLabel),
            Width = Dim.Fill(2)
        };

        var passwordLabel = new Label("Password:")
        {
            X = 1,
            Y = Pos.Bottom(userLabel) + 1,
            Width = 13
        };
        var passwordInput = new TextField("")
        {
            X = Pos.Right(passwordLabel),
            Y = Pos.Top(passwordLabel),
            Width = Dim.Fill(2),
            Secret = true
        };

        var registrationCodeLabel = new Label("Reg Code:")
        {
            X = 1,
            Y = Pos.Top(passwordLabel),
            Width = 13,
            Visible = false
        };
        var registrationCodeInput = new TextField("")
        {
            X = Pos.Right(registrationCodeLabel),
            Y = Pos.Top(registrationCodeLabel),
            Width = Dim.Fill(2),
            Visible = false
        };

        var empireLabel = new Label("Empire:")
        {
            X = 1,
            Y = Pos.Bottom(registrationCodeLabel) + 1,
            Width = 13,
            Visible = false
        };
        var empires = new List<string> { "solarian", "voidborn", "crimson", "nebula", "outerrim" };
        var empireDropdown = new ComboBox(empires)
        {
            X = Pos.Right(empireLabel),
            Y = Pos.Top(empireLabel),
            Width = Dim.Fill(2),
            Height = 1,
            ReadOnly = true,
            Text = empires[0],
            Visible = false
        };

        void ApplyMode()
        {
            bool isRegister = modeRadio.SelectedItem == 1;
            passwordLabel.Visible = !isRegister;
            passwordInput.Visible = !isRegister;
            registrationCodeLabel.Visible = isRegister;
            registrationCodeInput.Visible = isRegister;
            empireLabel.Visible = isRegister;
            empireDropdown.Visible = isRegister;
            dialog.SetNeedsDisplay();
        }

        modeRadio.SelectedItemChanged += _ => ApplyMode();
        ApplyMode();

        dialog.Add(
            modeLabel,
            modeRadio,
            userLabel,
            userInput,
            passwordLabel,
            passwordInput,
            registrationCodeLabel,
            registrationCodeInput,
            empireLabel,
            empireDropdown);

        bool submitted = false;
        ok.Clicked += () =>
        {
            bool isRegister = modeRadio.SelectedItem == 1;
            var username = userInput.Text.ToString() ?? "";
            var password = passwordInput.Text.ToString() ?? "";
            var registrationCode = registrationCodeInput.Text.ToString() ?? "";
            string empire = empires[0];
            if (empireDropdown.SelectedItem >= 0 && empireDropdown.SelectedItem < empires.Count)
                empire = empires[empireDropdown.SelectedItem];
            else if (!string.IsNullOrWhiteSpace(empireDropdown.Text?.ToString()))
                empire = empireDropdown.Text.ToString()!;

            if (!string.IsNullOrWhiteSpace(username))
            {
                if (!isRegister && !string.IsNullOrWhiteSpace(password))
                {
                    _addBotWriter?.TryWrite(new AddBotRequest(
                        AddBotMode.Login,
                        username,
                        Password: password));
                    submitted = true;
                }
                else if (isRegister &&
                         !string.IsNullOrWhiteSpace(registrationCode) &&
                         !string.IsNullOrWhiteSpace(empire))
                {
                    _addBotWriter?.TryWrite(new AddBotRequest(
                        AddBotMode.Register,
                        username,
                        RegistrationCode: registrationCode,
                        Empire: empire));
                    submitted = true;
                }
            }

            Application.RequestStop();
        };

        cancel.Clicked += () => Application.RequestStop();
        Application.Run(dialog);

        if (submitted)
            _input.SetFocus();
    }

    private void SaveCurrentScriptAsExample()
    {
        _saveExampleWriter?.TryWrite(true);
        _input.SetFocus();
    }

    private void OpenMissionPromptPicker()
    {
        if (_activeMissionPrompts.Count == 0)
        {
            MessageBox.Query("Active Missions", "No active missions available.", "OK");
            _input.SetFocus();
            return;
        }

        var useButton = new Button("Use Prompt");
        var cancelButton = new Button("Cancel");
        var dialog = new Dialog("Mission Prompt", 92, 17, useButton, cancelButton);

        var missionLabel = new Label("Mission:")
        {
            X = 1,
            Y = 1,
            Width = 10
        };

        var missionNames = _activeMissionPrompts
            .Select(m => m.Label)
            .ToList();

        var missionDropdown = new ComboBox(missionNames)
        {
            X = Pos.Right(missionLabel),
            Y = Pos.Top(missionLabel),
            Width = Dim.Fill(2),
            Height = 1,
            ReadOnly = true
        };

        var promptLabel = new Label("Prompt:")
        {
            X = 1,
            Y = Pos.Bottom(missionLabel) + 1,
            Width = 10
        };

        var promptPreview = new TextView()
        {
            X = Pos.Right(promptLabel),
            Y = Pos.Top(promptLabel),
            Width = Dim.Fill(2),
            Height = 7,
            ReadOnly = true,
            WordWrap = true
        };

        void RefreshPromptPreview()
        {
            int idx = missionDropdown.SelectedItem;
            if (idx < 0 || idx >= _activeMissionPrompts.Count)
                idx = 0;

            promptPreview.Text = _activeMissionPrompts[idx].Prompt;
        }

        missionDropdown.SelectedItemChanged += _ => RefreshPromptPreview();
        missionDropdown.SelectedItem = 0;
        RefreshPromptPreview();

        dialog.Add(missionLabel, missionDropdown, promptLabel, promptPreview);

        bool useSelection = false;
        useButton.Clicked += () =>
        {
            useSelection = true;
            Application.RequestStop();
        };
        cancelButton.Clicked += () => Application.RequestStop();

        Application.Run(dialog);

        if (useSelection)
        {
            int idx = missionDropdown.SelectedItem;
            if (idx < 0 || idx >= _activeMissionPrompts.Count)
                idx = 0;

            _input.Text = _activeMissionPrompts[idx].Prompt;
        }

        _input.SetFocus();
    }

    private void ExecuteScriptNow()
    {
        _executeScriptWriter?.TryWrite(true);
        _input.SetFocus();
    }

    private void ApplyScriptLayout()
    {
        _scriptFrame.ColorScheme = _scriptSideScheme;
        _memoryFrame.ColorScheme = _scriptSideScheme;
        _inputFrame.ColorScheme = _scriptSideScheme;
        _scriptFrame.Height = Dim.Percent(58);
        _memoryFrame.Height = Dim.Fill(5);
        _scriptFrame.Title = "Script";
        _editScriptButton.Text = "Edit Script";
        _executeButton.Visible = true;
        _saveExampleButton.Visible = true;
    }

    private static int CountRows(string text)
    {
        if (string.IsNullOrEmpty(text))
            return 1;

        int rows = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                rows++;
        }

        return rows;
    }

    private void ApplySelectedStateTabText()
    {
        bool showTradeTab = !string.IsNullOrWhiteSpace(_lastTradeStateMarkdown);
        bool showShipyardTab = !string.IsNullOrWhiteSpace(_lastShipyardStateMarkdown);
        bool showCantinaTab = !string.IsNullOrWhiteSpace(_lastCantinaStateMarkdown);
        var stateMarkdown = _selectedStateTab switch
        {
            StateTab.Trade when showTradeTab => _lastTradeStateMarkdown!,
            StateTab.Shipyard when showShipyardTab => _lastShipyardStateMarkdown!,
            StateTab.Cantina when showCantinaTab => _lastCantinaStateMarkdown!,
            _ => _lastSpaceStateMarkdown
        };

        int previousTopRow = _state.TopRow;
        int previousLeftColumn = _state.LeftColumn;

        string nextStateText = string.IsNullOrWhiteSpace(stateMarkdown)
            ? "(no state)"
            : stateMarkdown;

        if (!string.Equals(_lastRenderedStateMarkdown, nextStateText, StringComparison.Ordinal))
        {
            _state.Text = nextStateText;
            _lastRenderedStateMarkdown = nextStateText;
        }

        int visibleRows = Math.Max(1, _state.Bounds.Height - 1);
        int totalRows = CountRows(_state.Text?.ToString() ?? "");
        int maxTopRow = Math.Max(0, totalRows - visibleRows);
        _state.TopRow = Math.Min(previousTopRow, maxTopRow);
        _state.LeftColumn = Math.Max(0, previousLeftColumn);
    }

    private static void SetListSourcePreserveScroll(ListView view, IList source)
    {
        int previousTopItem = Math.Max(0, view.TopItem);
        int previousSelectedItem = Math.Max(0, view.SelectedItem);

        view.SetSource(source);

        int count = source.Count;
        if (count <= 0)
            return;

        int maxIndex = count - 1;
        view.TopItem = Math.Min(previousTopItem, maxIndex);
        view.SelectedItem = Math.Min(previousSelectedItem, maxIndex);
    }

    private void UpdateStateTabButtons(bool showTradeTab, bool showShipyardTab, bool showCantinaTab)
    {
        _tradeTabButton.Visible = showTradeTab;
        _shipyardTabButton.Visible = showShipyardTab;
        _cantinaTabButton.Visible = showCantinaTab;
        if (!showTradeTab && _selectedStateTab == StateTab.Trade)
            _selectedStateTab = StateTab.Space;
        if (!showShipyardTab && _selectedStateTab == StateTab.Shipyard)
            _selectedStateTab = StateTab.Space;
        if (!showCantinaTab && _selectedStateTab == StateTab.Cantina)
            _selectedStateTab = StateTab.Space;

        _spaceTabButton.ColorScheme = _selectedStateTab == StateTab.Space
            ? _activeBotScheme
            : _topInfoBar.ColorScheme;
        _tradeTabButton.ColorScheme = _selectedStateTab == StateTab.Trade
            ? _activeBotScheme
            : _topInfoBar.ColorScheme;
        _shipyardTabButton.ColorScheme = _selectedStateTab == StateTab.Shipyard
            ? _activeBotScheme
            : _topInfoBar.ColorScheme;
        _cantinaTabButton.ColorScheme = _selectedStateTab == StateTab.Cantina
            ? _activeBotScheme
            : _topInfoBar.ColorScheme;
    }

    private void RefreshPlayerStack(IReadOnlyList<BotTab> bots, string? activeBotId)
    {
        var labels = string.Join(",", bots.Select(b => b.Label));
        var signature = $"{bots.Count}|{activeBotId}|{labels}";
        if (signature == _lastPlayerSignature)
            return;

        if (signature != _lastPlayerSignature)
        {
            _lastPlayerSignature = signature;
            try
            {
                File.AppendAllText(
                    AppPaths.AuthFlowLogFile,
                    $"[{DateTime.UtcNow:O}] ui_refresh_player_stack | tabs_changed | count={bots.Count} | active={activeBotId ?? "(null)"} | labels=[{labels}]{Environment.NewLine}");
            }
            catch
            {
                // Debug logging must not break UI.
            }
        }

        foreach (var view in _playerControls)
            _playerStack.Remove(view);
        _playerControls.Clear();

        int y = 0;
        foreach (var bot in bots)
        {
            string label = bot.Label;
            string botId = bot.Id;

            var button = new Button(label)
            {
                X = 0,
                Y = y,
                Width = Dim.Fill(),
                Height = 1
            };
            if (bot.Id == activeBotId)
                button.ColorScheme = _activeBotScheme;
            button.Clicked += () => _switchBotWriter?.TryWrite(botId);

            _playerControls.Add(button);
            _playerStack.Add(button);
            y += 1;
        }

        if (bots.Count == 0)
        {
            var emptyLabel = new Label("(no bots loaded)")
            {
                X = 0,
                Y = 0
            };
            _playerControls.Add(emptyLabel);
            _playerStack.Add(emptyLabel);
            y = 1;
        }

        var addButton = new Button("+")
        {
            X = 0,
            Y = y + 1,
            Width = Dim.Fill(),
            Height = 1
        };
        addButton.Clicked += OpenAddBotModal;

        _playerControls.Add(addButton);
        _playerStack.Add(addButton);
    }
}
