using System.IO;
using System.Windows;
using System.Windows.Controls;
using NativeWidget.Models;
using NativeWidget.Services;

namespace NativeWidget;

public partial class HoverAiWindow
{
    private bool _loadingPromptPreset;
    private HoverAiPromptPreset? _selectedPromptPreset;

    private void InitializePromptPresets()
    {
        _config.HoverAiPromptPresets ??= new();
        PromptPresetsEnabledCheck.IsChecked = _config.HoverAiPromptPresetsEnabled;
        SelectPromptPreset(_config.HoverAiPromptPresets.FirstOrDefault(p => p.Id == _config.HoverAiSelectedPromptPresetId)
            ?? _config.HoverAiPromptPresets.FirstOrDefault());
    }

    private void SelectPromptPreset(HoverAiPromptPreset? preset)
    {
        _loadingPromptPreset = true;
        try
        {
            PromptPresetPicker.ItemsSource = null;
            PromptPresetPicker.ItemsSource = _config.HoverAiPromptPresets;
            PromptPresetPicker.SelectedItem = preset;
            LoadPromptPreset(preset);
        }
        finally { _loadingPromptPreset = false; }
    }

    private void LoadPromptPreset(HoverAiPromptPreset? preset)
    {
        _selectedPromptPreset = preset;
        _config.HoverAiSelectedPromptPresetId = preset?.Id ?? "";
        PromptPresetNameInput.Text = preset?.Name ?? "";
        PromptPresetTextInput.Text = preset?.Prompt ?? "";
        PromptPresetNameInput.IsEnabled = PromptPresetTextInput.IsEnabled = preset != null;
        PromptPresetSaveButton.IsEnabled = PromptPresetDuplicateButton.IsEnabled = PromptPresetDeleteButton.IsEnabled = preset != null;
    }

    private void PromptPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingPromptPreset || !_ready) return;
        _loadingPromptPreset = true;
        try { LoadPromptPreset(PromptPresetPicker.SelectedItem as HoverAiPromptPreset); }
        finally { _loadingPromptPreset = false; }
        PersistPromptPresets();
    }

    private void PromptPreset_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingPromptPreset || !_ready || _selectedPromptPreset == null) return;
        _selectedPromptPreset.Name = PromptPresetNameInput.Text;
        _selectedPromptPreset.Prompt = PromptPresetTextInput.Text;
        PromptPresetStatus.Text = "Changes save automatically.";
        _saveTimer.Stop(); _saveTimer.Start();
    }

    private void PromptPresetsEnabled_Click(object sender, RoutedEventArgs e)
    {
        _config.HoverAiPromptPresetsEnabled = PromptPresetsEnabledCheck.IsChecked == true;
        PersistPromptPresets();
    }

    private void PromptPresetNew_Click(object sender, RoutedEventArgs e)
    {
        var index = 1;
        while (_config.HoverAiPromptPresets.Any(p => p.Name == $"Prompt mới {index}")) index++;
        var preset = new HoverAiPromptPreset { Name = $"Prompt mới {index}" };
        _config.HoverAiPromptPresets.Add(preset);
        SelectPromptPreset(preset); PersistPromptPresets();
    }

    private void PromptPresetDuplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPromptPreset == null) return;
        var preset = new HoverAiPromptPreset { Name = _selectedPromptPreset.Name + " (copy)", Prompt = _selectedPromptPreset.Prompt };
        _config.HoverAiPromptPresets.Add(preset);
        SelectPromptPreset(preset); PersistPromptPresets();
    }

    private void PromptPresetDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPromptPreset == null) return;
        var index = _config.HoverAiPromptPresets.IndexOf(_selectedPromptPreset);
        _config.HoverAiPromptPresets.Remove(_selectedPromptPreset);
        SelectPromptPreset(_config.HoverAiPromptPresets.ElementAtOrDefault(Math.Min(index, _config.HoverAiPromptPresets.Count - 1)));
        PersistPromptPresets();
    }

    private void PromptPresetSave_Click(object sender, RoutedEventArgs e) => PersistPromptPresets();

    private void PersistPromptPresets()
    {
        try { HoverAiAppearanceStore.Save(_config); PromptPresetStatus.Text = "Saved on this computer. System prompt stays separate."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { PromptPresetStatus.Text = "Could not save prompts. Try Save again."; }
    }

    private string WithSelectedPromptPreset(string text)
    {
        var preset = _config.HoverAiPromptPresets.FirstOrDefault(p => p.Id == _config.HoverAiSelectedPromptPresetId);
        if (!_config.HoverAiPromptPresetsEnabled || string.IsNullOrWhiteSpace(preset?.Prompt)) return text;
        return "Áp dụng hướng dẫn sau chỉ cho câu trả lời của lượt này:\n" + preset.Prompt.Trim()
            + "\n\nNội dung cần xử lý (ảnh đính kèm nếu có):\n" + text;
    }
}
