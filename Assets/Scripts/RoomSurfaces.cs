using Meta.XR.MRUtilityKit;
using UnityEngine;

/// <summary>
/// Finds real-world flat surfaces (tables, floor, etc.) from the headset's room scan, via MR Utility Kit.
/// Returns nothing if there is no MRUK object in the scene or the room hasn't loaded yet.
/// </summary>
public static class RoomSurfaces
{
    const MRUKAnchor.SceneLabels SurfaceLabels =
        MRUKAnchor.SceneLabels.TABLE | MRUKAnchor.SceneLabels.FLOOR | MRUKAnchor.SceneLabels.COUCH |
        MRUKAnchor.SceneLabels.STORAGE | MRUKAnchor.SceneLabels.BED | MRUKAnchor.SceneLabels.OTHER;

    /// <summary>
    /// Finds the nearest upward-facing surface directly above or below the point, within maxDistance.
    /// </summary>
    public static bool TryFindSurface(Vector3 point, float maxDistance, out Vector3 surfacePoint)
    {
        surfacePoint = default;

        MRUK mruk = MRUK.Instance;
        if (mruk == null || !mruk.IsInitialized)
            return false;
        MRUKRoom room = mruk.GetCurrentRoom();
        if (room == null)
            return false;

        float bestDistance = float.MaxValue;
        foreach (MRUKAnchor anchor in room.Anchors)
        {
            if (!anchor.PlaneRect.HasValue || !anchor.HasAnyLabel(SurfaceLabels))
                continue;

            // MRUK planes lie in the anchor's local XY, with the surface normal along its forward axis.
            Transform t = anchor.transform;
            if (Vector3.Dot(t.forward, Vector3.up) < 0.95f)
                continue;

            Vector3 local = t.InverseTransformPoint(point);
            float distance = Mathf.Abs(local.z);
            if (distance > maxDistance || distance >= bestDistance)
                continue;
            if (!anchor.IsPositionInBoundary(new Vector2(local.x, local.y)))
                continue;

            bestDistance = distance;
            surfacePoint = point - t.forward * local.z;
        }

        return bestDistance < float.MaxValue;
    }
}
