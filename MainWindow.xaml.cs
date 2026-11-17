using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WellenEditor;

public partial class MainWindow : Window
{
    private AudioDocument? document;
    private IWavePlayer? player;
    private bool isPlaying;
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch playbackClock = new();
    private int playbackStartFrame;
    private int playbackFrameCount;
    private bool loopSelection;
    private int loopStartFrame;
    private int loopFrameCount;

    public MainWindow()
    {
        InitializeComponent();
        AllowDrop = true;
        DragOver += (_, e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) OpenAudio(files[0]); };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closing += (_, _) => StopPlayback();
        playbackTimer.Tick += PlaybackTimer_Tick;
        Loaded += (_, _) =>
        {
            var startupFile = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
            if (startupFile is not null) OpenAudio(startupFile);
        };
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Audiodateien|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac|Alle Dateien|*.*" };
        if (dialog.ShowDialog(this) == true) OpenAudio(dialog.FileName);
    }

    private void OpenAudio(string fileName)
    {
        try
        {
            StopPlayback(); document = AudioDocument.Load(fileName); Waveform.SetAudio(document.Samples, document.Channels, document.SampleRate);
            FileNameText.Text = Path.GetFileName(fileName); FileList.Items.Clear(); FileList.Items.Add(new ListBoxItem { Content = Path.GetFileName(fileName) });
            FormatText.Text = $"{document.SampleRate:N0} Hz · {ChannelText(document.Channels)} · 32-bit float";
            DurationText.Text = $"Länge: {FormatTime(document.Duration)}"; ViewRangeText.Text = $"0:00.000 – {FormatTime(document.Duration)}";
            HintText.Content = "Bereit. Mit der Maus einen Bereich markieren."; UpdateSelectionText();
        }
        catch (Exception exception) { MessageBox.Show(this, $"Die Datei konnte nicht geöffnet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (document is null) return;
        var dialog = new SaveFileDialog { Filter = "WAV 16-Bit PCM|*.wav|WAV 24-Bit PCM|*.wav|WAV 32-Bit Float|*.wav", FilterIndex = 2, FileName = $"{Path.GetFileNameWithoutExtension(document.SourcePath)}_bearbeitet.wav" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var bits = dialog.FilterIndex switch { 1 => 16, 3 => 32, _ => 24 };
            document.SaveAsWave(dialog.FileName, bits);
            HintText.Content = $"Gespeichert: {Path.GetFileName(dialog.FileName)} ({bits}-Bit)";
        }
        catch (Exception exception) { MessageBox.Show(this, $"Die Datei konnte nicht gespeichert werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (document is null) return;
        if (isPlaying) { StopPlayback(); return; }
        var selection = Waveform.GetSelection(); var start = selection?.StartSample ?? Waveform.CursorSample; var count = selection?.Length ?? document.FrameCount - start;
        if (count <= 0) return;
        loopSelection = selection.HasValue;
        loopStartFrame = start;
        loopFrameCount = count;
        try
        {
            StartPlayback(start, count);
            HintText.Content = selection.HasValue ? "Schleifenwiedergabe der Auswahl." : "Wiedergabe ab Cursorposition.";
        }
        catch (Exception exception) { StopPlayback(); MessageBox.Show(this, $"Die Wiedergabe konnte nicht gestartet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static IWavePlayer CreatePlayer()
    {
        using var enumerator = new MMDeviceEnumerator();
        var output = string.IsNullOrWhiteSpace(AudioHardwareSettings.OutputDeviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(AudioHardwareSettings.OutputDeviceId);
        return new WasapiOut(output, AudioClientShareMode.Shared, false, 100);
    }

    private void Player_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.Exception is not null)
            {
                StopPlayback();
                HintText.Content = $"Wiedergabefehler: {e.Exception.Message}";
            }
            else if (loopSelection && isPlaying && document is not null)
            {
                StartPlayback(loopStartFrame, loopFrameCount);
                HintText.Content = "Schleifenwiedergabe der Auswahl.";
            }
            else
            {
                StopPlayback();
                HintText.Content = "Wiedergabe beendet.";
            }
        });
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void StopPlayback()
    {
        loopSelection = false;
        isPlaying = false;
        playbackClock.Stop(); playbackTimer.Stop();
        DisposePlayer();
    }

    private void StartPlayback(int start, int count)
    {
        DisposePlayer();
        playbackStartFrame = start;
        playbackFrameCount = count;
        Waveform.SetCursor(start);
        player = CreatePlayer();
        player.Init(document!.CreateWaveProvider(start, count));
        player.PlaybackStopped += Player_PlaybackStopped;
        player.Play();
        isPlaying = true;
        playbackClock.Restart();
        playbackTimer.Start();
    }

    private void DisposePlayer()
    {
        var oldPlayer = player;
        player = null;
        if (oldPlayer is null) return;
        oldPlayer.PlaybackStopped -= Player_PlaybackStopped; oldPlayer.Stop(); oldPlayer.Dispose();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (document is null || !isPlaying) return;
        var playedFrames = (int)(playbackClock.Elapsed.TotalSeconds * document.SampleRate);
        var frame = Math.Min(playbackStartFrame + playedFrames, playbackStartFrame + playbackFrameCount);
        Waveform.SetCursor(frame);
        HintText.Content = $"▶ Wiedergabe: {FormatTime(TimeSpan.FromSeconds(frame / (double)document.SampleRate))}";
    }

    private void Cut_Click(object sender, RoutedEventArgs e) { CopySelection(); DeleteSelection(); }
    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelection();
    private void CopySelection()
    {
        if (document is null || Waveform.GetSelection() is not Selection selection) return;
        AudioClipboard.Set(document.Extract(selection)); HintText.Content = "Auswahl kopiert.";
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection();
    private void DeleteSelection()
    {
        if (document is null || Waveform.GetSelection() is not Selection selection) return;
        StopPlayback(); document.Delete(selection); RefreshAfterEdit(selection.StartSample);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (document is null || AudioClipboard.Current is not AudioSelectionData audio) return;
        try { StopPlayback(); var point = Waveform.CursorSample; document.Insert(point, audio); RefreshAfterEdit(point + audio.Samples.Length / audio.Channels); }
        catch (InvalidOperationException exception) { MessageBox.Show(this, exception.Message, "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { if (document?.CanUndo == true) { StopPlayback(); document.Undo(); RefreshAfterEdit(); } }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (document?.CanRedo == true) { StopPlayback(); document.Redo(); RefreshAfterEdit(); } }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => Waveform.SelectAll();
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Waveform.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Waveform.ZoomOut();
    private void ZoomVerticalIn_Click(object sender, RoutedEventArgs e) => Waveform.ZoomVerticalIn();
    private void ZoomVerticalOut_Click(object sender, RoutedEventArgs e) => Waveform.ZoomVerticalOut();
    private void ShowAll_Click(object sender, RoutedEventArgs e) => Waveform.ShowAll();

    private void Normalize_Click(object sender, RoutedEventArgs e) { if (document is not null) ApplyEffect(document.Normalize, "Normalisierung abgeschlossen."); }
    private void FadeIn_Click(object sender, RoutedEventArgs e) { if (document is not null) ApplyEffect(document.FadeIn, "Einblenden angewendet."); }
    private void FadeOut_Click(object sender, RoutedEventArgs e) { if (document is not null) ApplyEffect(document.FadeOut, "Ausblenden angewendet."); }
    private void ApplyEffect(Action<Selection?>? effect, string message)
    {
        if (document is null || effect is null) return;
        StopPlayback(); effect(Waveform.GetSelection()); RefreshAfterEdit(); HintText.Content = message;
    }

    private void RefreshAfterEdit(int? cursor = null)
    {
        if (document is null) return;
        Waveform.UpdateAudio(document.Samples, document.Channels, document.SampleRate); if (cursor.HasValue) Waveform.SetCursor(cursor.Value);
        DurationText.Text = $"Länge: {FormatTime(document.Duration)}"; ViewRangeText.Text = $"0:00.000 – {FormatTime(document.Duration)}"; UpdateSelectionText();
        UpdateUndoRedoState();
    }

    private void Waveform_SelectionChanged(object? sender, EventArgs e) => UpdateSelectionText();
    private void UpdateSelectionText()
    {
        if (document is null || Waveform.GetSelection() is not Selection selection) { StartText.Content = "Start: —"; EndText.Content = "Ende: —"; SelectionText.Content = "Auswahl: —"; SampleText.Content = $"Samples: {Waveform.CursorSample:N0} · keine Auswahl"; return; }
        StartText.Content = $"Start: {FormatTime(TimeSpan.FromSeconds(selection.StartSample / (double)document.SampleRate))}";
        EndText.Content = $"Ende: {FormatTime(TimeSpan.FromSeconds(selection.EndSample / (double)document.SampleRate))}";
        SelectionText.Content = $"Auswahl: {FormatTime(TimeSpan.FromSeconds(selection.Length / (double)document.SampleRate))}";
        SampleText.Content = $"Samples: {selection.StartSample:N0} – {selection.EndSample:N0} ({selection.Length:N0})";
    }

    private void UpdateUndoRedoState()
    {
        if (document is null) { HintText.Content = "Keine Datei geladen."; return; }
        HintText.Content = document.CanUndo ? "Rückgängig möglich." : "Keine Rückgängig-Schritte.";
    }

    private void AudioHardware_Click(object sender, RoutedEventArgs e) { var dialog = new AudioHardwareWindow { Owner = this }; if (dialog.ShowDialog() == true) HintText.Content = "Audio-Hardwarezuordnung übernommen."; }
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "WaveBench Pro\nKlassische Einzelspuren-Bearbeitung für Windows 11", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (document is null) return;
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        var key = e.Key;

        if ((key == System.Windows.Input.Key.Space || key == System.Windows.Input.Key.Tab)) { Play_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (key == System.Windows.Input.Key.Delete) DeleteSelection();
        else if (key == System.Windows.Input.Key.A && modifiers == System.Windows.Input.ModifierKeys.Control) Waveform.SelectAll();
        else if (key == System.Windows.Input.Key.Z && modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) Redo_Click(this, new RoutedEventArgs());
            else Undo_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (key == System.Windows.Input.Key.Y && modifiers == System.Windows.Input.ModifierKeys.Control) { Redo_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (key == System.Windows.Input.Key.Left || key == System.Windows.Input.Key.Right)
        {
            var step = key == System.Windows.Input.Key.Right ? 1 : -1;
            if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) step *= 100;
            Waveform.MoveCursor(step, modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (key == System.Windows.Input.Key.Home) { Waveform.SetCursor(0); e.Handled = true; }
        else if (key == System.Windows.Input.Key.End) { Waveform.SetCursor(document.FrameCount); e.Handled = true; }
    }

    private static string FormatTime(TimeSpan duration) => duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss\.fff") : duration.ToString(@"m\:ss\.fff");
    private static string ChannelText(int channels) => channels == 1 ? "Mono" : channels == 2 ? "Stereo" : $"{channels} Kanäle";
}
