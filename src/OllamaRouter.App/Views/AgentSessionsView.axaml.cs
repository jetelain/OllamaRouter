using Avalonia.Controls;
using OllamaRouter.App.ViewModels;

namespace OllamaRouter.App.Views;

/// <summary>
/// The Agents &amp; Sessions tab view (task #124): the fleet-wide summary, the per-agent
/// session table (selection drives the detail panel) and the per-target breakdown of the
/// selected session.
/// </summary>
public partial class AgentSessionsView : UserControl
{
    /// <summary>Creates the view (the DataContext is the <see cref="AgentSessionsViewModel"/>).</summary>
    public AgentSessionsView()
    {
        InitializeComponent();
    }
}
