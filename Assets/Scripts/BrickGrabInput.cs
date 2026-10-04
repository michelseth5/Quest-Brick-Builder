using Oculus.Interaction;
using Oculus.Interaction.Grab;
using Oculus.Interaction.HandGrab;
using Oculus.Interaction.Input;
using UnityEngine;

/// <summary>
/// Decides how a brick was grabbed:
///   Controllers: index trigger = single brick, grip (middle finger) = whole connected group.
///   Hands:       pinch = single brick, whole-hand grab = whole connected group.
/// </summary>
public static class BrickGrabInput
{
    // The Meta rig only grabs with the grip button by default. Let the index trigger grab too.
    // Only close-range grabbing is changed; grabbing from a distance stays on the grip.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnableTriggerGrab()
    {
        GrabInteractor[] interactors =
            Object.FindObjectsByType<GrabInteractor>(FindObjectsInactive.Include, FindObjectsSortMode.None);

        foreach (GrabInteractor interactor in interactors)
        {
            foreach (ControllerSelector selector in interactor.GetComponentsInChildren<ControllerSelector>(true))
            {
                if ((selector.ControllerButtonUsage & ControllerButtonUsage.GripButton) != 0)
                    selector.ControllerButtonUsage |= ControllerButtonUsage.TriggerButton;
            }
        }
    }

    public static bool IsWholeGroupGrab(PointerEvent evt)
    {
        // Hands (and controller-driven hands): the grab type is reported directly.
        if (evt.Data is IHandGrabState handGrab)
            return handGrab.HandGrabTarget.Anchor == GrabTypeFlags.Palm;

        // Controllers: check whether the grip is held on the controller that grabbed.
        if (evt.Data is Component interactor)
        {
            ControllerSelector selector = interactor.GetComponentInChildren<ControllerSelector>(true);
            if (selector != null && selector.Controller != null)
                return selector.Controller.IsButtonUsageAnyActive(ControllerButtonUsage.GripButton);
        }

        return false;
    }
}
