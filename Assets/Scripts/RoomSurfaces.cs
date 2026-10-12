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
            return TryFloor(point, maxDistance, out surfacePoint);
        MRUKRoom room = mruk.GetCurrentRoom();
        if (room == null)
            return TryFloor(point, maxDistance, out surfacePoint);

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

        if (bestDistance < float.MaxValue)
            return true;
        return TryFloor(point, maxDistance, out surfacePoint);
    }

    static OVRCameraRig s_rig;

    // The headset's own floor height (its tracking space is floor-level), for when the room scan has no
    // floor here: no scan yet, or standing outside the scanned room.
    static bool TryFloor(Vector3 point, float maxDistance, out Vector3 surfacePoint)
    {
        surfacePoint = default;
        if (OVRManager.instance == null || OVRManager.instance.trackingOriginType == OVRManager.TrackingOrigin.EyeLevel)
            return false;
        if (s_rig == null)
            s_rig = Object.FindFirstObjectByType<OVRCameraRig>();
        if (s_rig == null || s_rig.trackingSpace == null)
            return false;

        float floor = s_rig.trackingSpace.position.y;
        if (Mathf.Abs(point.y - floor) > maxDistance)
            return false;
        surfacePoint = new Vector3(point.x, floor, point.z);
        return true;
    }
}
