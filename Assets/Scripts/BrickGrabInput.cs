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
    // Runs at startup and again whenever the scene is reloaded (Start Over).
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void Initialize()
    {
        EnableTriggerGrab();
        DisableComfortTunneling();
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (scene, mode) =>
        {
            EnableTriggerGrab();
            DisableComfortTunneling();
        };
    }

    // The rig's locomotion comes with a comfort vignette that narrows the view whenever the head gets near a
    // collider (meant for walls), and every brick has one, so leaning in to a model closed the view in.
    // There's no walking in this game, so it's switched off.
    static void DisableComfortTunneling()
    {
        foreach (var wall in Object.FindObjectsByType<Oculus.Interaction.Locomotion.WallPenetrationTunneling>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            wall.enabled = false;
        foreach (var moving in Object.FindObjectsByType<Oculus.Interaction.Locomotion.LocomotionTunneling>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            moving.enabled = false;
        foreach (TunnelingEffect effect in Object.FindObjectsByType<TunnelingEffect>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            effect.enabled = false;
            foreach (Renderer renderer in effect.GetComponentsInChildren<Renderer>(true))
                renderer.enabled = false;
        }
    }

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
