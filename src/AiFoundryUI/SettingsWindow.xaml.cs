using System.Windows;
using AiFoundryUI.Models;

namespace AiFoundryUI;

public partial class SettingsWindow : Window
{
    private readonly Config _config;

    public SettingsWindow(Config cfg)
    {
        InitializeComponent();
        _config = cfg;
        TxtDefaultInstructions.Text = _config.DefaultInstructions ?? string.Empty;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        _config.DefaultInstructions = TxtDefaultInstructions.Text ?? string.Empty;
        Config.Save(_config);
        this.DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        this.DialogResult = false;
        Close();
    }
}
