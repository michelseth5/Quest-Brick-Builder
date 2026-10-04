using System.Collections;
using UnityEngine;

/// <summary>
/// Moves this object to a spot in front of the user's head when the app starts,
/// so it doesn't depend on where the headset's tracking origin happens to be.
/// </summary>
public class PlaceInFrontOfUser : MonoBehaviour
{
    [Tooltip("How far in front of the head, in meters.")]
    public float distance = 0.4f;

    [Tooltip("How far below eye level, in meters.")]
    public float dropBelowEyes = 0.25f;

    IEnumerator Start()
    {
        // Head tracking reports a zero pose for the first few frames; wait for a real one.
        Camera head = null;
        float timeout = 3f;
        while (timeout > 0f)
        {
            head = Camera.main;
            if (head != null && head.transform.position != Vector3.zero)
                break;
            timeout -= Time.deltaTime;
            yield return null;
        }

        if (head == null)
        {
            Debug.LogWarning("PlaceInFrontOfUser: no camera tagged MainCamera found.");
            yield break;
        }

        // Use only the horizontal look direction so the object doesn't end up in the floor or ceiling.
        Vector3 forward = Vector3.ProjectOnPlane(head.transform.forward, Vector3.up).normalized;
        if (forward == Vector3.zero)
            forward = Vector3.forward;

        transform.position = head.transform.position + forward * distance + Vector3.down * dropBelowEyes;
        transform.rotation = Quaternion.LookRotation(forward, Vector3.up);
    }
}
