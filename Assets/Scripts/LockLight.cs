using UnityEngine;

/// <summary>
/// A small light on each controller that shows, while that hand is holding something, whether it's locked:
/// red = every piece of the held model is locked, green = none are, amber = a locked model with loose
/// pieces added (press X to lock them in). Off when the hand is empty.
/// </summary>
public class LockLight : MonoBehaviour
{
    // Where the light sits on the controller (meters, in the hand anchor's space): just above its top face.
    static readonly Vector3 Offset = new Vector3(0f, 0.022f, 0.012f);
    const float Size = 0.007f;

    static readonly Color Locked = new Color(1f, 0.12f, 0.1f);
    static readonly Color Unlocked = new Color(0.15f, 1f, 0.25f);
    static readonly Color Partly = new Color(1f, 0.7f, 0.1f);

    readonly GameObject[] _lights = new GameObject[2];
    readonly MeshRenderer[] _renderers = new MeshRenderer[2];
    readonly int[] _shown = { -1, -1 };

    void LateUpdate()
    {
        for (int hand = 0; hand < 2; hand++)
        {
            Brick held = null;
            foreach (Brick brick in Brick.Held)
            {
                if (brick != null && brick.HeldHand == hand)
                {
                    held = brick;
                    break;
                }
            }

            Transform anchor = WristInventory.HandTransform(hand);
            if (held == null || anchor == null)
            {
                if (_lights[hand] != null && _lights[hand].activeSelf)
                    _lights[hand].SetActive(false);
                continue;
            }

            if (_lights[hand] == null)
                Create(hand, anchor);
            if (!_lights[hand].activeSelf)
                _lights[hand].SetActive(true);

            (int locked, int total) = held.LockedCount();
            int state = locked == 0 ? 0 : locked == total ? 2 : 1;
            if (state != _shown[hand])
            {
                _shown[hand] = state;
                MenuStyle.Tint(_renderers[hand], state == 0 ? Unlocked : state == 2 ? Locked : Partly);
            }
        }
    }

    void Create(int hand, Transform anchor)
    {
        GameObject light = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        light.name = hand == 0 ? "Lock Light Left" : "Lock Light Right";
        Destroy(light.GetComponent<Collider>());
        light.transform.SetParent(anchor, worldPositionStays: false);
        light.transform.localPosition = Offset;
        light.transform.localScale = Vector3.one * Size;

        _renderers[hand] = light.GetComponent<MeshRenderer>();
        _renderers[hand].sharedMaterial = MenuStyle.Material;
        _renderers[hand].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        _lights[hand] = light;
        _shown[hand] = -1;
    }

    void OnDestroy()
    {
        foreach (GameObject light in _lights)
        {
            if (light != null)
                Destroy(light);
        }
    }
}
