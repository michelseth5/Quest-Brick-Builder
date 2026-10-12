using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Undo for building: each grab records where the moved bricks were (and which model they were bonded
/// into). Undo puts them back. Bricks taken out of a menu are removed again, and trashed bricks come
/// back (they're hidden, not deleted, until they fall out of the history).
/// </summary>
public static class UndoHistory
{
    const int MaxSteps = 30;

    public class Step
    {
        internal readonly List<(Brick brick, Transform parent, Vector3 position, Quaternion rotation, Brick.Bond bond)> poses =
            new List<(Brick, Transform, Vector3, Quaternion, Brick.Bond)>();
        internal readonly List<Brick> spawned = new List<Brick>();
        internal Brick trashedRoot;
        internal readonly List<Brick> trashed = new List<Brick>();
    }

    static readonly List<Step> Steps = new List<Step>();

    /// <summary>Raised for each trashed brick brought back by Undo.</summary>
    public static event Action<Brick> Restored;

    public static bool CanUndo => Steps.Count > 0;

    /// <summary>Forgets everything (e.g. when starting over).</summary>
    public static void Clear() => Steps.Clear();

    /// <summary>Starts a step remembering where these bricks are now.</summary>
    public static Step RecordMove(IEnumerable<Brick> bricks)
    {
        var step = new Step();
        foreach (Brick brick in bricks)
            AddPose(step, brick, brick.transform.parent);
        Push(step);
        return step;
    }

    /// <summary>Remembers a brick's pose relative to the parent it should go back under.</summary>
    public static void AddPose(Step step, Brick brick, Transform parent)
    {
        Transform t = brick.transform;
        Vector3 position = parent != null ? parent.InverseTransformPoint(t.position) : t.position;
        Quaternion rotation = parent != null ? Quaternion.Inverse(parent.rotation) * t.rotation : t.rotation;
        step.poses.Add((brick, parent, position, rotation, brick.CurrentBond));
    }

    /// <summary>A step whose undo removes these newly created bricks.</summary>
    public static Step RecordSpawn(IEnumerable<Brick> bricks)
    {
        var step = new Step();
        step.spawned.AddRange(bricks);
        Push(step);
        return step;
    }

    /// <summary>The step's bricks went in the trash: undo brings them back (hidden until then).</summary>
    public static void MarkTrashed(Step step, Brick root, IEnumerable<Brick> all)
    {
        if (step == null)
        {
            step = new Step();
            Push(step);
        }
        step.trashedRoot = root;
        step.trashed.AddRange(all);
    }

    /// <summary>Whether the step is "this brick was just taken out of a menu".</summary>
    public static bool IsSpawnOf(Step step, Brick brick) => step != null && step.spawned.Contains(brick);

    /// <summary>Drops a step that can't sensibly be undone (e.g. the brick went onto a wrist).</summary>
    public static void Discard(Step step)
    {
        if (step != null)
            Steps.Remove(step);
    }

    static void Push(Step step)
    {
        Steps.Add(step);
        while (Steps.Count > MaxSteps)
        {
            Forget(Steps[0]);
            Steps.RemoveAt(0);
        }
    }

    // A step leaving the history: trashed bricks it was keeping are deleted for real.
    static void Forget(Step step)
    {
        if (step.trashedRoot != null && !step.trashedRoot.gameObject.activeSelf)
            Object.Destroy(step.trashedRoot.gameObject);
    }

    /// <summary>Undoes the latest step that still applies. False if there was nothing to undo.</summary>
    public static bool Undo()
    {
        while (Steps.Count > 0)
        {
            Step step = Steps[Steps.Count - 1];
            Steps.RemoveAt(Steps.Count - 1);
            if (Apply(step))
                return true;
        }
        return false;
    }

    static bool Apply(Step step)
    {
        bool changed = false;

        foreach (Brick brick in step.spawned)
        {
            if (brick == null || brick.IsStored || brick.IsLocked)
                continue;
            brick.NotifyTrashed(); // e.g. the Parts Bin takes it back
            Object.Destroy(brick.gameObject);
            changed = true;
        }

        if (step.trashedRoot != null && !step.trashedRoot.gameObject.activeSelf)
        {
            step.trashedRoot.gameObject.SetActive(true);
            foreach (Brick brick in step.trashed)
                Restored?.Invoke(brick);
            changed = true;
        }

        foreach ((Brick brick, Transform parent, Vector3 position, Quaternion rotation, Brick.Bond bond) in step.poses)
        {
            if (brick == null || brick.IsStored || brick.IsLocked || brick.IsPreview)
                continue;
            Transform t = brick.transform;
            t.SetParent(parent, worldPositionStays: true);
            if (parent != null)
                t.SetPositionAndRotation(parent.TransformPoint(position), parent.rotation * rotation);
            else
                t.SetPositionAndRotation(position, rotation);
            brick.RejoinBond(bond);
            changed = true;
        }
        return changed;
    }
}
