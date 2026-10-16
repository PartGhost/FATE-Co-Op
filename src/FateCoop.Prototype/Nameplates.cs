using System.Linq;
using HarmonyLib;
using Tableflip;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace FateCoop.Prototype;

public sealed partial class Plugin
{
    private const float NameplateHeight = 2.8f, PlateBarWidth = 160f, PlateBarHeight = 8f, PlateBarGap = 4f, ArrowSize = 26f, ArrowInset = 0.97f;
    private const string ArrowSprite = "Btn_Arrow-Yellow_Idle";
    private static readonly Color ManaColor = new Color(0.25f, 0.5f, 1f);
    private static readonly AccessTools.FieldRef<PlayerUI, GameObject> MinimapRef = AccessTools.FieldRefAccess<PlayerUI, GameObject>("minimapUI");
    private Sprite? arrowSprite;

    private void UpdateNameplates()
    {
        if (hudOwner == null) return;
        var shown = showPanel && !MenuOpen();
        foreach (var partner in partners.Values)
        {
            if (partner.Actor == null || partner.Frame == null) continue;
            if (partner.Plate == null) BuildNameplate(partner);
            partner.Plate!.SetActive(shown);
            partner.HpFill!.localScale = new Vector3(Fraction(partner.Frame.hp, partner.Frame.maxHp), 1f, 1f);
            partner.ManaFill!.localScale = new Vector3(Fraction(partner.Frame.mana, partner.Frame.maxMana), 1f, 1f);
        }
        UpdateMinimapArrows();
    }

    private static float Fraction(int value, int max) => max > 0 ? Mathf.Clamp01((float)value / max) : 0f;

    private void BuildNameplate(Partner partner)
    {
        var tooltip = SingletonBehaviour<FateDB>.Instance.portalPrefab.GetComponentInChildren<InteractableTooltipCanvas>(true);
        var plate = Object.Instantiate(tooltip.gameObject, partner.Actor!.transform);
        plate.transform.localPosition = Vector3.up * NameplateHeight;
        var label = plate.GetComponent<InteractableTooltipCanvas>();
        label.SetText(partner.Name);
        label.SetIsHovered(false);
        partner.Plate = plate;
        partner.HpFill = PlateBar(plate.transform, 0, null);
        partner.ManaFill = PlateBar(plate.transform, 1, ManaColor);
    }

    private RectTransform PlateBar(Transform plate, int row, Color? tint)
    {
        var source = TargetBarRef(hudOwner!);
        var bar = (RectTransform)Object.Instantiate(source.hpBarFill.parent.gameObject, plate).transform;
        bar.anchorMin = bar.anchorMax = new Vector2(0.5f, 0f);
        bar.pivot = new Vector2(0.5f, 1f);
        bar.sizeDelta = new Vector2(PlateBarWidth, PlateBarHeight);
        bar.anchoredPosition = new Vector2(0f, -row * (PlateBarHeight + PlateBarGap));
        bar.localScale = Vector3.one;
        bar.GetChild(source.poisonBarFill.GetSiblingIndex()).gameObject.SetActive(false);
        var fill = (RectTransform)bar.GetChild(source.hpBarFill.GetSiblingIndex());
        if (tint.HasValue)
        {
            var image = fill.GetComponent<Image>();
            image.sprite = null;
            image.color = tint.Value;
        }
        return fill;
    }

    private void UpdateMinimapArrows()
    {
        var minimap = MinimapRef(hudOwner!);
        var player = Player.PlayerChar;
        var camera = player != null ? player.GetComponentsInChildren<MiniMapIconUI>(true).FirstOrDefault(icon => icon.isCamera)?.GetComponent<Camera>() : null;
        var image = minimap != null ? minimap.GetComponentInChildren<RawImage>(true) : null;
        var key = LocalFrameWorld();
        foreach (var partner in partners.Values)
        {
            var visible = camera != null && image != null && minimap!.activeInHierarchy && partner.InWorld(key) && partner.Frame != null;
            if (!visible)
            {
                if (partner.Arrow != null) partner.Arrow.gameObject.SetActive(false);
                continue;
            }
            var root = minimap!.transform;
            var radius = root.InverseTransformVector(image!.rectTransform.TransformVector(Vector3.right * image.rectTransform.rect.width / 2f)).magnitude;
            var viewport = camera!.WorldToViewportPoint(partner.Frame!.primaryPosition);
            var offset = new Vector2(viewport.x - 0.5f, viewport.y - 0.5f) * 2f * radius;
            var arrow = partner.Arrow != null ? partner.Arrow : partner.Arrow = BuildArrow(root);
            arrow.gameObject.SetActive(offset.magnitude > radius * ArrowInset);
            var direction = offset.normalized;
            arrow.anchoredPosition = direction * radius * ArrowInset;
            arrow.localEulerAngles = new Vector3(0f, 0f, Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg + 180f);
        }
    }

    private RectTransform BuildArrow(Transform minimap)
    {
        arrowSprite ??= Resources.FindObjectsOfTypeAll<Sprite>().First(sprite => sprite.name == ArrowSprite);
        var arrow = new GameObject("FateCoop_Friend_Arrow", typeof(RectTransform), typeof(Image));
        arrow.transform.SetParent(minimap, false);
        arrow.GetComponent<Image>().sprite = arrowSprite;
        arrow.GetComponent<Image>().raycastTarget = false;
        var rect = (RectTransform)arrow.transform;
        rect.sizeDelta = new Vector2(ArrowSize, ArrowSize);
        return rect;
    }
}
