namespace DeskPane.Models;

public sealed record FileWidgetTransientState(
    string[] SelectedPaths,
    string[] CutPaths);
