using System.Windows.Controls;
using HashLynx.UI.ViewModels;

namespace HashLynx.UI.Views;

public partial class AttackView : UserControl
{
    private AttackViewModel? _subscribed;
    public AttackView()
    {
        InitializeComponent();
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
        DataContextChanged += (_, _) => { Unsubscribe(); if (IsLoaded) Subscribe(); };
    }
    private void Subscribe()
    {
        Unsubscribe();
        if (DataContext is AttackViewModel viewModel) { _subscribed = viewModel; viewModel.FocusRequested += FocusSection; }
    }
    private void Unsubscribe()
    {
        if (_subscribed is not null) _subscribed.FocusRequested -= FocusSection;
        _subscribed = null;
    }
    private void FocusSection(string section)
    {
        if (section == "target") TargetSection.BringIntoView();
        else InputsSection.BringIntoView();
    }
}
