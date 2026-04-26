using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using GeminiChat.Services;

namespace GeminiChat.ViewModels
{
    // ─────────────────────────────────────────────
    //  UI Message Model
    // ─────────────────────────────────────────────

    /// <summary>
    /// Represents a single chat bubble in the UI.
    /// Separate from the API ChatMessage — this one holds UI state.
    /// </summary>
    public class UiMessage : INotifyPropertyChanged
    {
        private string _text = string.Empty;
        private bool   _isStreaming;

        public ChatRole Role         { get; init; }
        public DateTime Timestamp    { get; init; } = DateTime.Now;
        public bool     IsUser       => Role == ChatRole.User;
        public bool     IsModel      => Role == ChatRole.Model;

        public string Text
        {
            get => _text;
            set { _text = value; OnPropertyChanged(); }
        }

        /// <summary>True while the model is still typing (streaming).</summary>
        public bool IsStreaming
        {
            get => _isStreaming;
            set { _isStreaming = value; OnPropertyChanged(); }
        }

        public string TimeLabel => Timestamp.ToString("HH:mm");

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ─────────────────────────────────────────────
    //  RelayCommand — no extra NuGet needed
    // ─────────────────────────────────────────────

    public class RelayCommand : ICommand
    {
        private readonly Action<object?>  _execute;
        private readonly Func<object?, bool>? _canExecute;

        public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
        {
            _execute    = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
        public void Execute(object? parameter)    => _execute(parameter);

        public event EventHandler? CanExecuteChanged
        {
            add    => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        /// <summary>Manually trigger CanExecute re-evaluation.</summary>
        public void RaiseCanExecuteChanged()
            => CommandManager.InvalidateRequerySuggested();
    }

    public class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Func<object?, bool>? _canExecute;
        private bool _isRunning;

        public AsyncRelayCommand(Func<object?, Task> execute, Func<object?, bool>? canExecute = null)
        {
            _execute    = execute;
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
            => !_isRunning && (_canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object? parameter)
        {
            _isRunning = true;
            CommandManager.InvalidateRequerySuggested();
            try   { await _execute(parameter); }
            finally
            {
                _isRunning = false;
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public event EventHandler? CanExecuteChanged
        {
            add    => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }

    // ─────────────────────────────────────────────
    //  MainViewModel
    // ─────────────────────────────────────────────

    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        // ── Fields ───────────────────────────────

        private readonly GeminiService _gemini;
        private CancellationTokenSource? _cts;

        private string  _inputText    = string.Empty;
        private bool    _isBusy;
        private string  _statusText   = "Siap";
        private bool    _hasError;

        // ── Constructor ──────────────────────────

        public MainViewModel()
        {
            // ── Configure your API key here ──────────────────────────────────
            // Best practice: load from environment variable or appsettings.json
            // Example: Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? ""
            // -----------------------------------------------------------------
            var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                         ?? "AIzaSyD-lChvglvW1tArfnMehNj4U4g0p__d78c";

            _gemini = new GeminiService(new GeminiServiceOptions
            {
                ApiKey               = apiKey,
                Model                = "gemini-2.5-flash",
                SystemPrompt         = "Kamu adalah asisten AI yang ramah, membantu, dan utamanya menjawab dalam Bahasa Inggris atau Bahasa Indonesia sesuai bahasa yang digunakan user.",
                Temperature          = 0.9f,
                MaxOutputTokens      = 8192,
                ContextWindowMessages = 30,
            });

            // ── Commands ─────────────────────────
            SendCommand  = new AsyncRelayCommand(OnSendAsync,  _ => !IsBusy && !string.IsNullOrWhiteSpace(InputText));
            StopCommand  = new RelayCommand(_ => OnStop(),     _ => IsBusy);
            ClearCommand = new RelayCommand(_ => OnClear(),    _ => Messages.Count > 0 && !IsBusy);        }

        // ── Bindable Properties ──────────────────

        /// <summary>The chat message list bound to the UI ListBox/ItemsControl.</summary>
        public ObservableCollection<UiMessage> Messages { get; } = [];

        /// <summary>Text in the input box.</summary>
        public string InputText
        {
            get => _inputText;
            set
            {
                _inputText = value;
                OnPropertyChanged();
                // Refresh Send button state when text changes
                CommandManager.InvalidateRequerySuggested();
            }
        }

        /// <summary>True while waiting for / streaming an AI response.</summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                _isBusy = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotBusy));
                CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool IsNotBusy => !_isBusy;

        /// <summary>Status bar text at the bottom of the window.</summary>
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        /// <summary>True when the last operation ended in an error.</summary>
        public bool HasError
        {
            get => _hasError;
            private set { _hasError = value; OnPropertyChanged(); }
        }

        // ── Commands ─────────────────────────────

        public ICommand SendCommand  { get; }
        public ICommand StopCommand  { get; }
        public ICommand ClearCommand { get; }

        // ── Command Handlers ─────────────────────

        private async Task OnSendAsync(object? _)
        {
            var userText = InputText.Trim();
            if (string.IsNullOrEmpty(userText)) return;

            // Add user bubble & clear input
            AddUserMessage(userText);
            InputText = string.Empty;

            // Prepare an empty model bubble for streaming
            var modelBubble = new UiMessage
            {
                Role        = ChatRole.Model,
                Text        = "",
                IsStreaming = true,
            };
            Messages.Add(modelBubble);

            IsBusy     = true;
            HasError   = false;
            StatusText = "Mengetik…";

            _cts = new CancellationTokenSource();

            try
            {
                // ── Streaming ─────────────────────────────────────────────────
                await foreach (var chunk in _gemini.StreamMessageAsync(userText, _cts.Token))
                {
                    modelBubble.Text += chunk;
                }
            }
            catch (OperationCanceledException)
            {
                if (string.IsNullOrEmpty(modelBubble.Text))
                    modelBubble.Text = "_(dihentikan)_";
                else
                    modelBubble.Text += " _(dihentikan)_";

                StatusText = "Dihentikan.";
            }
            catch (GeminiException ex)
            {
                modelBubble.Text = $"⚠️ Error {ex.StatusCode}: {ex.Message}";
                StatusText       = $"Error: {ex.Message}";
                HasError         = true;
            }
            catch (Exception ex)
            {
                modelBubble.Text = $"⚠️ Terjadi kesalahan: {ex.Message}";
                StatusText       = "Terjadi kesalahan.";
                HasError         = true;
            }
            finally
            {
                modelBubble.IsStreaming = false;
                IsBusy                 = false;
                _cts?.Dispose();
                _cts = null;

                if (!HasError && StatusText == "Mengetik…")
                    StatusText = "Siap";
            }
        }

        private void OnStop()
        {
            _cts?.Cancel();
            StatusText = "Menghentikan…";
        }

        private void OnClear()
        {
            _gemini.ClearHistory();
            Messages.Clear();
            StatusText = "Percakapan dihapus.";
            HasError   = false;

            AddModelMessage("Percakapan baru dimulai. Ada yang bisa saya bantu? 😊");
        }

        // ── Helpers ──────────────────────────────

        private void AddUserMessage(string text)
            => Messages.Add(new UiMessage { Role = ChatRole.User,  Text = text });

        private void AddModelMessage(string text)
            => Messages.Add(new UiMessage { Role = ChatRole.Model, Text = text });

        // ── INotifyPropertyChanged ────────────────

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        // ── IDisposable ──────────────────────────

        public void Dispose()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _gemini.Dispose();
        }
    }
}
