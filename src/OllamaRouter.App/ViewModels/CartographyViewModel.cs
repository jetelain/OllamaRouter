using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using OllamaRouter.App.ViewModels.InstallModelWizard;
using OllamaRouter.Client;
using OllamaRouter.Endpoints.RouterApi.Cartography;

namespace OllamaRouter.App.ViewModels;

/// <summary>
/// One cell of the model × target cartography matrix (task #102, spec §5.3 — wire:
/// <see cref="CartographyDeploymentRow"/>).
/// </summary>
public sealed record CartographyCellRow(
    string Target,
    bool Present,
    bool Capable,
    int ContextCeiling,
    string? VetoReason,
    string? Quantization)
{
    /// <summary>The single-line state summary used for the cell tooltip / sub-text: the
    /// capable/present status (or the veto reason, when not capable) plus the
    /// quantization (when known), joined with " · ".</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            if (VetoReason is not null)
            {
                parts.Add(VetoReason);
            }
            else if (Capable)
            {
                parts.Add("capable");
            }
            else if (Present)
            {
                parts.Add("present, not capable");
            }
            else
            {
                parts.Add("not present");
            }

            if (Quantization is not null)
            {
                parts.Add(Quantization);
            }

            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// One model row of the cartography matrix (task #102, spec §5.3 — wire:
/// <see cref="CartographyModelRow"/> + its deployments flattened to
/// <see cref="Cells"/> in fleet-node order).
/// </summary>
public sealed partial class CartographyModelRowVm
{
    public CartographyModelRowVm(
        CartographyModelRow wire,
        IReadOnlyList<CartographyNodeRow> nodes)
    {
        Wire = wire;
        Cells = BuildCells(wire, nodes);
    }

    /// <summary>The wire model row.</summary>
    public CartographyModelRow Wire { get; }

    /// <summary>The model name.</summary>
    public string Name => Wire.Name;

    /// <summary>The architecture (e.g. "llama" / "moe").</summary>
    public string Architecture => Wire.Architecture;

    /// <summary>The weight size, humanized (e.g. "4.2 GB").</summary>
    public string WeightSizeHuman => VramBytes.FormatBytes(Wire.WeightSizeBytes);

    /// <summary>The native context window in tokens.</summary>
    public int NativeContextWindow => Wire.NativeContextWindow;

    /// <summary>The recommended context window in tokens.</summary>
    public int RecommendedContextWindow => Wire.RecommendedContextWindow;

    /// <summary>Whether cloud overflow is available for the model.</summary>
    public bool CloudOverflowAvailable => Wire.CloudOverflowAvailable;

    /// <summary>The catalog-sourced quantization (when known).</summary>
    public string? Quantization => Wire.Quantization;

    /// <summary>The one deployment cell per fleet node (node order).</summary>
    public IReadOnlyList<CartographyCellRow> Cells { get; }

    /// <summary>Whether the model is present on at least one fleet target (drives the
    /// row-level Uninstall button's visibility — task #126; Cloud can never be present: the
    /// matrix cells are per effective target only, the Cloud pseudo-target is dropped).</summary>
    public bool HasPresentTargets => Cells.Any(c => c.Present);

    private static IReadOnlyList<CartographyCellRow> BuildCells(
        CartographyModelRow wire, IReadOnlyList<CartographyNodeRow> nodes)
    {
        var byTarget = wire.Deployments.ToDictionary(d => d.Target, StringComparer.OrdinalIgnoreCase);
        var cells = new List<CartographyCellRow>(nodes.Count);
        foreach (var node in nodes)
        {
            byTarget.TryGetValue(node.Name, out var deployment);
            cells.Add(new CartographyCellRow(
                node.Name,
                deployment?.Present ?? false,
                deployment?.Capable ?? false,
                deployment?.ContextCeiling ?? 0,
                deployment?.VetoReason,
                deployment?.Quantization));
        }

        return cells;
    }
}

/// <summary>
/// Byte-size humanization shared by the cartography rows (strictly display-only).
/// </summary>
internal static class VramBytes
{
    /// <summary>Formats a byte count as a human size (B / KB / MB / GB / TB, 3 significant
    /// decimals as needed).</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        double value = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        int unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024.0;
            unit++;
        }

        return $"{value.ToString("0.##", CultureInfo.InvariantCulture)} {units[unit]}";
    }
}

/// <summary>
/// The Model Cartography &amp; Preload tab view model (task #102, spec §5.3): the model ×
/// target deployment matrix (5 s poll from <c>GET /api/router/cartography</c>), the loaded-
/// process list, the per-model preload action (the dialog is presented by the view; the
/// <see cref="PreloadDialogViewModel"/> owns the preview-then-preload logic) and the per-
/// model uninstall action (task #126; the <see cref="UninstallDialogViewModel"/> owns the
/// target selection + per-target results).
/// </summary>
public sealed partial class CartographyViewModel : RouterTabViewModel
{
    /// <summary>The Cartography tab polling interval (spec §4).</summary>
    internal static readonly TimeSpan CartographyInterval = TimeSpan.FromSeconds(5);

    /// <summary>The wire name of the Cloud pseudo-target the server appends to the
    /// cartography nodes (<c>RoutingTarget.Cloud.Value</c>); excluded from the matrix.</summary>
    internal const string CloudPseudoTargetName = "Cloud";

    public CartographyViewModel(
        Func<IOllamaRouterApiClient> clientAccessor,
        Func<TimeSpan, CancellationToken, Task>? pollDelay = null)
        : base(clientAccessor, pollDelay)
    {
    }

    /// <inheritdoc />
    protected override TimeSpan PollInterval => CartographyInterval;

    /// <summary>The fleet node rows (matrix column order = node order, Cloud pseudo-target
    /// excluded).</summary>
    public ObservableCollection<CartographyNodeRow> Nodes { get; } = [];

    /// <summary>The model rows with their deployment cells (matrix rows).</summary>
    public ObservableCollection<CartographyModelRowVm> Models { get; } = [];

    /// <summary>The flat loaded-process list (cartesian flatten of nodes × loaded models).</summary>
    public ObservableCollection<CartographyLoadedModelRow> LoadedProcesses { get; } = [];

    private static async Task RunOnUiAsync(Action action)
    {
        try
        {
            if (Avalonia.Application.Current is null || Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
            {
                action();
            }
            else
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(action);
            }
        }
        catch
        {
            action();
        }
    }

    /// <inheritdoc />
    protected override async Task RefreshCoreAsync(CancellationToken ct)
    {
        var response = await ClientAccessor().GetCartographyAsync(ct).ConfigureAwait(false);

        // The server appends the Cloud pseudo-target as the last node row; it carries no
        // per-model deployment information, so the matrix keeps only the real fleet targets.
        var matrixNodes = response.Nodes
            .Where(node => !string.Equals(node.Name, CloudPseudoTargetName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        await RunOnUiAsync(() =>
        {
            Nodes.Clear();
            foreach (var node in matrixNodes)
            {
                Nodes.Add(node);
            }

            Models.Clear();
            foreach (var model in response.Models)
            {
                Models.Add(new CartographyModelRowVm(model, matrixNodes));
            }

            LoadedProcesses.Clear();
            foreach (var loaded in response.LoadedModels)
            {
                LoadedProcesses.Add(loaded);
            }
        });
    }

    /// <summary>Creates the preload dialog view model for a model (the view shows the
    /// dialog window).</summary>
    public PreloadDialogViewModel CreatePreloadDialog(string modelName) =>
        new(modelName, ClientAccessor);

    /// <summary>Creates the Install Model wizard view model (task #125; the view shows the
    /// modal wizard window).</summary>
    public InstallModelWizardViewModel CreateInstallModelWizard() =>
        new(ClientAccessor);

    /// <summary>Creates the uninstall dialog view model for a model row (task #126): the
    /// dialog lists EXACTLY the targets where the model is present (pre-checked); the view
    /// shows the dialog window and confirms the destructive action.</summary>
    public UninstallDialogViewModel CreateUninstallDialog(CartographyModelRowVm row) =>
        new(
            row.Name,
            row.Cells.Where(c => c.Present).Select(c => c.Target).ToList(),
            ClientAccessor);
}
