using System;
using System.Collections.Generic;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal static class PathingArtifactPickupContext
{
    private static readonly HashSet<string> EnabledScriptFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "AAA-Artifacts-Bulk-Supply",
        "ArtifactsGroupPurchasing"
    };

    private static readonly string[] ArtifactNames = ["游医", "幸运儿", "冒险家"];
    private const string KazuhaName = "枫原万叶";

    private static readonly object LockObject = new();
    private static readonly Dictionary<string, int> ArtifactPickupCounts = new();
    private static string? _currentWaypointKey;

    public static void Reset()
    {
        lock (LockObject)
        {
            _currentWaypointKey = null;
            ArtifactPickupCounts.Clear();
        }
    }

    public static void SetCurrentWaypoint(int segmentIndex, int waypointIndex, WaypointForTrack waypoint)
    {
        if (!IsEnabledForCurrentScript())
        {
            return;
        }

        lock (LockObject)
        {
            _currentWaypointKey = BuildWaypointKey(segmentIndex, waypointIndex, waypoint);
        }
    }

    public static bool RecordPickupText(string text)
    {
        if (!IsEnabledForCurrentScript() || !ContainsArtifactName(text))
        {
            return false;
        }

        lock (LockObject)
        {
            if (_currentWaypointKey is null)
            {
                return false;
            }

            ArtifactPickupCounts.TryGetValue(_currentWaypointKey, out var count);
            ArtifactPickupCounts[_currentWaypointKey] = count + 1;
            return count + 1 >= 2;
        }
    }

    public static void RecordPickupLogMessage(string? message, object?[]? args)
    {
        if (string.IsNullOrEmpty(message) || !message.Contains("交互或拾取", StringComparison.Ordinal))
        {
            return;
        }

        RecordPickupText(message);
        if (args is null)
        {
            return;
        }

        foreach (var arg in args)
        {
            var text = arg?.ToString();
            if (!string.IsNullOrEmpty(text))
            {
                RecordPickupText(text);
            }
        }
    }

    public static bool ShouldSkipKazuhaCombatScript(WaypointForTrack waypoint)
    {
        if (waypoint.Action != ActionEnum.CombatScript.Code || !HasCurrentWaypointDuplicateArtifact())
        {
            return false;
        }

        return waypoint.CombatScript?.AvatarNames.Contains(KazuhaName) == true
               || (waypoint.ActionParams?.Contains("万叶", StringComparison.OrdinalIgnoreCase) == true)
               || (waypoint.ActionParams?.Contains("Kazuha", StringComparison.OrdinalIgnoreCase) == true);
    }

    public static bool ShouldSkipCurrentWaypointKazuhaStrategy()
    {
        return HasCurrentWaypointDuplicateArtifact();
    }

    public static bool IsKazuhaCommand(string command)
    {
        try
        {
            var alias = BetterGenshinImpact.GameTask.AutoFight.Config.DefaultAutoFightConfig.AvatarAliasToStandardName(GetCommandCharacterName(command));
            return string.Equals(alias, KazuhaName, StringComparison.Ordinal);
        }
        catch
        {
            return command.Contains("万叶", StringComparison.OrdinalIgnoreCase)
                   || command.Contains("Kazuha", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool HasCurrentWaypointDuplicateArtifact()
    {
        if (!IsEnabledForCurrentScript())
        {
            return false;
        }

        lock (LockObject)
        {
            return _currentWaypointKey is not null
                   && ArtifactPickupCounts.TryGetValue(_currentWaypointKey, out var count)
                   && count >= 2;
        }
    }

    private static bool IsEnabledForCurrentScript()
    {
        var project = TaskContext.Instance().CurrentScriptProject;
        return project?.Type == "Javascript" && EnabledScriptFolders.Contains(project.FolderName);
    }

    private static bool ContainsArtifactName(string text)
    {
        foreach (var artifactName in ArtifactNames)
        {
            if (text.Contains(artifactName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildWaypointKey(int segmentIndex, int waypointIndex, WaypointForTrack waypoint)
    {
        return FormattableString.Invariant($"{segmentIndex}:{waypointIndex}:{waypoint.MapName}:{waypoint.GameX:F3}:{waypoint.GameY:F3}");
    }

    private static string GetCommandCharacterName(string command)
    {
        var trimmed = command.Trim();
        var dashIndex = trimmed.IndexOf('-');
        var spaceIndex = trimmed.IndexOf(' ');
        var endIndex = dashIndex > 0 ? dashIndex : spaceIndex;
        return endIndex > 0 ? trimmed[..endIndex] : trimmed;
    }
}
