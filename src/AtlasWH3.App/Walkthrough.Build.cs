namespace AtlasWH3.App;

public static partial class Walkthroughs
{
    private static readonly Tour Build = new("build", "Build",
    [
        new("build.empty", "No project yet",
            "A project (.atlaswh3) says which map and kit to build and how. Create one for the current map or open an existing one; " +
            "the rest of this tour covers the window once a project is open."),
        new("build.projectBar", "Project",
            "The open project's name, plus Open, New and Save. Ctrl+S saves the ticks and project settings."),
        new("build.runBar", "Run the build",
            "Build all (F5) runs every ticked step from top to bottom; Run selected runs only the highlighted row. " +
            "Pack only re-packs the last output, and Cancel stops at the next safe point."),
        new("build.progress", "Progress",
            "Shows which step is running and how far the build got."),
        new("build.tree", "Build steps",
            "Every step of the build in the order it runs: validate, your custom steps, the native compile steps, pack and install. " +
            "Untick a step to skip it; hover a row for what it writes and when it needs re-running."),
        new("build.logTab", "Log",
            "Each step's output while it runs. A failed step's error is shown here first."),
        new("build.logTools", "Log tools",
            "Filter the log, show only the selected row's lines, copy it, or open the log file, output folder and pack file."),
        new("build.settingsTab", "Project settings",
            "The map, assembly kit, tile map source, compile options, custom steps, what goes into the pack and where it is installed."),
        new("build.walkthrough", "Replay this tour",
            "This button (or F1) shows the walkthrough again."),
    ]);
}
